using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal enum LegacyBundleSubphase { RootRenamePending, RootRenamed, ParentRecreated, FinalMoved, ManifestReplaced, MarkerWritten, CatalogCompleted }
    internal sealed record LegacyBundleJournal(byte[] LegacyManifestSha256, string LegacyStagingLocator, string ReplacementTempLocator, LegacyBundleSubphase Subphase);

    // Legacy adoption has one durable ownership boundary: no filesystem mutation is
    // permitted until the closed plan and its unique root claim are committed together.
    internal RetentionCaptureMutationDisposition PlanLegacySensitiveBundle(IRetentionFileCaptureCapability capability, RetentionFileCapturePlanInput plan, byte[] legacyManifestSha256)
    {
        if (capability is null || plan is null || !plan.IsValid || legacyManifestSha256 is not { Length: 32 }) return RetentionCaptureMutationDisposition.Conflict;
        using var c = OpenExisting(); using var t = c.BeginTransaction(); var row = Load(c, t, capability.CaptureId);
        if (row is null || !row.Matches(capability) || row.Phase != RetentionCapturePhase.Reserved) { t.Commit(); return RetentionCaptureMutationDisposition.StaleNoOp; }
        try
        {
            foreach (var member in plan.Members) FileCaptureExec(c, t, "INSERT INTO retention_file_capture_members(capture_id,ordinal,relative_path,member_kind,byte_length,sha256,deletion_order) VALUES($id,$ordinal,$path,$kind,$length,$hash,$order);", ("$id", row.CaptureId), ("$ordinal", member.Ordinal), ("$path", member.RelativePath), ("$kind", MemberKind(member.Kind)), ("$length", member.ByteLength ?? (object)DBNull.Value), ("$hash", member.Sha256 ?? (object)DBNull.Value), ("$order", member.DeletionOrder));
            fileCaptureCheckpoint?.Invoke("legacy_plan_members_inserted");
            FileCaptureExec(c, t, "UPDATE retention_file_capture_reservations SET marker_sha256=$marker,manifest_sha256=$manifest,phase='staging',durable_cursor=0,planned_member_count=$count,planned_total_bytes=$bytes,updated_at=$updated WHERE capture_id=$id AND phase='reserved';", ("$marker", plan.MarkerSha256), ("$manifest", plan.ManifestSha256), ("$count", plan.Members.Count), ("$bytes", plan.TotalBytes), ("$updated", Timestamp(timeProvider.GetUtcNow())), ("$id", row.CaptureId));
            fileCaptureCheckpoint?.Invoke("legacy_plan_reservation_staged");
            var temporary = Path.Combine(row.Final, $".manifest.retention.{row.CaptureId}.tmp");
            FileCaptureExec(c, t, "INSERT INTO retention_legacy_bundle_journal(capture_id,root_locator,legacy_manifest_sha256,legacy_staging_locator,replacement_temp_locator,subphase) VALUES($id,$root,$digest,$staging,$temporary,'root_rename_pending');", ("$id", row.CaptureId), ("$root", row.Parent), ("$digest", legacyManifestSha256), ("$staging", row.Staging), ("$temporary", temporary));
            fileCaptureCheckpoint?.Invoke("legacy_plan_journal_inserted");
            t.Commit();
            fileCaptureCheckpoint?.Invoke("legacy_plan_journal_claimed");
            return RetentionCaptureMutationDisposition.Applied;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { t.Rollback(); return RetentionCaptureMutationDisposition.StaleNoOp; }
    }

    internal LegacyBundleJournal? LoadLegacyBundleJournal(IRetentionFileCaptureCapability capability)
    {
        if (capability is null) return null;
        using var c = OpenExisting(); using var t = c.BeginTransaction(); var row = Load(c, t, capability.CaptureId);
        if (row is null || !row.Matches(capability)) return null;
        using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT legacy_manifest_sha256,legacy_staging_locator,replacement_temp_locator,subphase FROM retention_legacy_bundle_journal WHERE capture_id=$id;"; q.Parameters.AddWithValue("$id", row.CaptureId);
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        return new((byte[])r[0], r.GetString(1), r.GetString(2), r.GetString(3) switch { "root_rename_pending" => LegacyBundleSubphase.RootRenamePending, "root_renamed" => LegacyBundleSubphase.RootRenamed, "parent_recreated" => LegacyBundleSubphase.ParentRecreated, "final_moved" => LegacyBundleSubphase.FinalMoved, "manifest_replaced" => LegacyBundleSubphase.ManifestReplaced, "marker_written" => LegacyBundleSubphase.MarkerWritten, "catalog_completed" => LegacyBundleSubphase.CatalogCompleted, _ => throw new RetentionMigrationBlockedException() });
    }

    internal RetentionCaptureMutationDisposition AdvanceLegacyBundleSubphase(IRetentionFileCaptureCapability capability, LegacyBundleSubphase expected, LegacyBundleSubphase next)
    {
        if (capability is null || next != expected + 1) return RetentionCaptureMutationDisposition.Conflict;
        using var c = OpenExisting(); using var t = c.BeginTransaction(); var row = Load(c, t, capability.CaptureId);
        if (row is null || !row.Matches(capability)) { t.Commit(); return RetentionCaptureMutationDisposition.StaleNoOp; }
        using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "UPDATE retention_legacy_bundle_journal SET subphase=$next WHERE capture_id=$id AND subphase=$expected;"; q.Parameters.AddWithValue("$id", row.CaptureId); q.Parameters.AddWithValue("$expected", LegacySubphase(expected)); q.Parameters.AddWithValue("$next", LegacySubphase(next));
        var changed = q.ExecuteNonQuery(); t.Commit(); return changed == 1 ? RetentionCaptureMutationDisposition.Applied : RetentionCaptureMutationDisposition.StaleNoOp;
    }
    private static string LegacySubphase(LegacyBundleSubphase phase) => phase switch { LegacyBundleSubphase.RootRenamePending => "root_rename_pending", LegacyBundleSubphase.RootRenamed => "root_renamed", LegacyBundleSubphase.ParentRecreated => "parent_recreated", LegacyBundleSubphase.FinalMoved => "final_moved", LegacyBundleSubphase.ManifestReplaced => "manifest_replaced", LegacyBundleSubphase.MarkerWritten => "marker_written", _ => "catalog_completed" };

    internal void RecordLegacySensitiveBundleBlocker(string rootLocator)
    {
        if (string.IsNullOrWhiteSpace(rootLocator)) throw new ArgumentException("Invalid retention file capture parent.", nameof(rootLocator));
        using var c = OpenExisting(); using var t = c.BeginTransaction();
        FileCaptureExec(c, t, "INSERT INTO retention_legacy_bundle_blockers(root_locator,classification,recorded_at) VALUES($root,'legacy_bundle_unverifiable',$at) ON CONFLICT(root_locator) DO NOTHING;", ("$root", Path.GetFullPath(rootLocator)), ("$at", Timestamp(timeProvider.GetUtcNow())));
        t.Commit();
    }
    internal RetentionFileCaptureReservation ReserveSensitiveBundle(string parentLocator, DateTimeOffset? reservedAt = null, string? finalLocator = null, bool legacyV1 = false, string? captureId = null)
    {
        if (context is null) throw new RetentionCatalogUnavailableException();
        if (string.IsNullOrWhiteSpace(parentLocator)) throw new ArgumentException("Invalid retention file capture parent.", nameof(parentLocator));
        try { parentLocator = Path.GetFullPath(parentLocator); } catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException("Invalid retention file capture parent.", nameof(parentLocator)); }
        if (captureId is not null && !CanonicalId(captureId)) throw new ArgumentException("Invalid retention file capture id.", nameof(captureId));
        var now = reservedAt ?? timeProvider.GetUtcNow(); var id = captureId ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); var token = RandomNumberGenerator.GetBytes(32); var reserved = Timestamp(now);
        var final = legacyV1 ? Path.Combine(parentLocator, id) : finalLocator ?? Path.Combine(parentLocator, id);
        var staging = legacyV1 ? Path.Combine(Path.GetDirectoryName(parentLocator)!, $".{id}.legacy-staging") : Path.Combine(parentLocator, $".{id}.staging");
        using var c = OpenExisting(); using var t = c.BeginTransaction(); var store = StoreId(c, t); if (store != context.StoreInstanceId) throw new RetentionCatalogUnavailableException();
        FileCaptureExec(c, t, "INSERT INTO retention_file_capture_reservations(capture_id,store_instance_id,store_kind,source_item_id,reserved_at,reserved_at_utc_ticks,policy_id,policy_version,parent_locator,staging_locator,final_locator,owner_token,marker_sha256,manifest_sha256,phase,durable_cursor,planned_member_count,planned_total_bytes,error_code,updated_at,legacy_v1) VALUES($id,$store,'sensitive_bundle',$id,$reserved,$ticks,'sensitive-bundle-7d',1,$parent,$staging,$final,$token,NULL,NULL,'reserved',NULL,0,0,NULL,$updated,$legacy);", ("$id", id), ("$store", store), ("$reserved", reserved), ("$ticks", now.UtcDateTime.Ticks), ("$parent", parentLocator), ("$staging", staging), ("$final", final), ("$token", token), ("$updated", reserved), ("$legacy", legacyV1 ? 1 : 0));
        t.Commit(); return new(id, store, RetentionCapturePhase.Reserved, parentLocator, staging, final, token, reserved, now.UtcDateTime.Ticks, null, null, []);
    }

    internal RetentionCaptureMutationDisposition PlanSensitiveBundle(IRetentionFileCaptureCapability capability, RetentionFileCapturePlanInput plan)
    {
        if (capability is null || plan is null || !plan.IsValid) return RetentionCaptureMutationDisposition.Conflict;
        using var c = OpenExisting(); using var t = c.BeginTransaction(); var row = Load(c, t, capability.CaptureId);
        if (row is null || !row.Matches(capability)) { t.Commit(); return RetentionCaptureMutationDisposition.StaleNoOp; }
        if (row.Phase != RetentionCapturePhase.Reserved) { t.Commit(); return RetentionCaptureMutationDisposition.StaleNoOp; }
        foreach (var m in plan.Members) FileCaptureExec(c, t, "INSERT INTO retention_file_capture_members(capture_id,ordinal,relative_path,member_kind,byte_length,sha256,deletion_order) VALUES($id,$ordinal,$path,$kind,$length,$hash,$order);", ("$id", row.CaptureId), ("$ordinal", m.Ordinal), ("$path", m.RelativePath), ("$kind", MemberKind(m.Kind)), ("$length", m.ByteLength ?? (object)DBNull.Value), ("$hash", m.Sha256 ?? (object)DBNull.Value), ("$order", m.DeletionOrder));
        fileCaptureCheckpoint?.Invoke("plan_members_inserted");
        FileCaptureExec(c, t, "UPDATE retention_file_capture_reservations SET marker_sha256=$marker,manifest_sha256=$manifest,phase='staging',durable_cursor=0,planned_member_count=$count,planned_total_bytes=$bytes,updated_at=$updated WHERE capture_id=$id AND phase='reserved';", ("$marker", plan.MarkerSha256), ("$manifest", plan.ManifestSha256), ("$count", plan.Members.Count), ("$bytes", plan.TotalBytes), ("$updated", Timestamp(timeProvider.GetUtcNow())), ("$id", row.CaptureId));
        t.Commit(); return RetentionCaptureMutationDisposition.Applied;
    }

    internal RetentionCaptureMutationDisposition TransitionSensitiveBundle(IRetentionFileCaptureCapability capability, RetentionCapturePhase expected, RetentionCapturePhase next, int cursor)
    {
        if (capability is null) return RetentionCaptureMutationDisposition.Conflict; var captureId = capability.CaptureId;
        if (!CanonicalId(captureId) || cursor < 0 || (expected, next) != (RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog)) return RetentionCaptureMutationDisposition.Conflict;
        using var c = OpenExisting(); using var t = c.BeginTransaction();
        using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "UPDATE retention_file_capture_reservations SET phase='published_pending_catalog',updated_at=$at WHERE capture_id=$id AND owner_token=$token AND phase='staging' AND marker_sha256 IS NOT NULL AND manifest_sha256 IS NOT NULL AND planned_member_count > 0 AND durable_cursor=planned_member_count AND durable_cursor=$cursor;"; q.Parameters.AddWithValue("$cursor", cursor); q.Parameters.AddWithValue("$at", Timestamp(timeProvider.GetUtcNow())); q.Parameters.AddWithValue("$id", captureId); q.Parameters.AddWithValue("$token", capability.OwnerToken); var count = q.ExecuteNonQuery(); t.Commit(); return count == 1 ? RetentionCaptureMutationDisposition.Applied : RetentionCaptureMutationDisposition.StaleNoOp;
    }

    internal RetentionCaptureMutationDisposition AdvanceSensitiveBundleCursor(IRetentionFileCaptureCapability capability, int expectedCursor)
    {
        if (capability is null) return RetentionCaptureMutationDisposition.Conflict; var captureId = capability.CaptureId;
        if (!CanonicalId(captureId) || expectedCursor < 0) return RetentionCaptureMutationDisposition.Conflict;
        using var c = OpenExisting(); using var t = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "UPDATE retention_file_capture_reservations SET durable_cursor=durable_cursor+1,updated_at=$at WHERE capture_id=$id AND owner_token=$token AND phase='staging' AND durable_cursor=$expected AND durable_cursor < planned_member_count;";
        q.Parameters.AddWithValue("$at", Timestamp(timeProvider.GetUtcNow())); q.Parameters.AddWithValue("$id", captureId); q.Parameters.AddWithValue("$token", capability.OwnerToken); q.Parameters.AddWithValue("$expected", expectedCursor); var count = q.ExecuteNonQuery(); t.Commit(); return count == 1 ? RetentionCaptureMutationDisposition.Applied : RetentionCaptureMutationDisposition.StaleNoOp;
    }

    internal RetentionCaptureMutationDisposition CompleteSensitiveBundle(IRetentionFileCaptureCapability capability, byte[] marker, byte[] manifest)
    {
        if (capability is null) return RetentionCaptureMutationDisposition.Conflict; var captureId = capability.CaptureId;
        if (!CanonicalId(captureId) || marker is not { Length: 32 } || manifest is not { Length: 32 }) return RetentionCaptureMutationDisposition.Conflict;
        using var c = OpenExisting(); using var t = c.BeginTransaction(); var row = Load(c, t, captureId);
        if (row is null || !row.Matches(capability)) { t.Commit(); return RetentionCaptureMutationDisposition.StaleNoOp; }
        if (row.MarkerSha256 is null || row.ManifestSha256 is null || !RetentionOwnershipReceipt.Matches(row.MarkerSha256, marker) || !RetentionOwnershipReceipt.Matches(row.ManifestSha256, manifest)) { t.Commit(); return RetentionCaptureMutationDisposition.Conflict; }
        if (row.Phase == RetentionCapturePhase.Complete) { t.Commit(); return RetentionCaptureMutationDisposition.NoOpAlreadyFinalized; }
        if (row.Phase != RetentionCapturePhase.PublishedPendingCatalog) { t.Commit(); return RetentionCaptureMutationDisposition.StaleNoOp; }
        var receipt = RetentionOwnershipReceipt.CreateSensitiveBundle(new(row.Store, row.CaptureId, row.ReservedAt, row.Ticks, row.MarkerSha256, row.ManifestSha256, row.Token)); var expires = Timestamp(DateTimeOffset.Parse(row.ReservedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) + RetentionV1Constants.SensitiveBundleTtl); var item = Guid.NewGuid().ToString("N");
        FileCaptureExec(c, t, "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,private_locator,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES($item,$store,'sensitive_bundle',$source,1,$receipt,$locator,$captured,$expires,'sensitive-bundle-7d',1,'expiring',1,1);", ("$item", item), ("$store", row.Store), ("$source", row.CaptureId), ("$receipt", receipt), ("$locator", row.Final), ("$captured", row.ReservedAt), ("$expires", expires));
        FileCaptureExec(c, t, "INSERT INTO retention_capture_journal(item_id,phase,durable_cursor) VALUES($item,'complete',NULL);", ("$item", item));
        fileCaptureCheckpoint?.Invoke("completion_item_and_journal_inserted");
        FileCaptureExec(c, t, "UPDATE retention_file_capture_reservations SET phase='complete',updated_at=$at WHERE capture_id=$id AND phase='published_pending_catalog';", ("$at", Timestamp(timeProvider.GetUtcNow())), ("$id", captureId)); t.Commit(); return RetentionCaptureMutationDisposition.Applied;
    }

    internal RetentionFileCaptureRecoverySnapshot? LoadIncompleteSensitiveBundle(string captureId) => !CanonicalId(captureId) ? null : LoadSnapshot(captureId);
    internal IReadOnlyList<RetentionFileCaptureRecoverySnapshot> FindIncompleteSensitiveBundles(int limit)
    {
        if (limit < 1 || limit > RetentionV1Constants.MaximumFileMembers) throw new ArgumentOutOfRangeException(nameof(limit)); using var c = OpenExisting(); using var t = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT capture_id FROM retention_file_capture_reservations WHERE phase <> 'complete' AND error_code IS NULL ORDER BY updated_at,capture_id LIMIT $limit;"; q.Parameters.AddWithValue("$limit", limit); using var r = q.ExecuteReader(); var ids = new List<string>(); while (r.Read()) ids.Add(r.GetString(0)); r.Close(); return ids.Select(id => Snapshot(Load(c, t, id)!)).ToArray();
    }
    internal RetentionCaptureMutationDisposition AbandonReservedSensitiveBundle(IRetentionFileCaptureCapability capability)
    {
        if (capability is null) return RetentionCaptureMutationDisposition.Conflict; var captureId=capability.CaptureId;
        if (!CanonicalId(captureId)) return RetentionCaptureMutationDisposition.Conflict; using var c = OpenExisting(); using var t = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "DELETE FROM retention_file_capture_reservations WHERE capture_id=$id AND owner_token=$token AND phase='reserved' AND planned_member_count=0 AND durable_cursor IS NULL;"; q.Parameters.AddWithValue("$id", captureId);q.Parameters.AddWithValue("$token",capability.OwnerToken); var count = q.ExecuteNonQuery(); t.Commit(); return count == 1 ? RetentionCaptureMutationDisposition.Applied : RetentionCaptureMutationDisposition.StaleNoOp;
    }
    internal RetentionCaptureMutationDisposition RecordSensitiveBundleBlocker(IRetentionFileCaptureCapability capability, RetentionErrorCode code)
    {
        if (capability is null) return RetentionCaptureMutationDisposition.Conflict;var captureId=capability.CaptureId;
        if (!CanonicalId(captureId) || code is not (RetentionErrorCode.CaptureIncomplete or RetentionErrorCode.ItemLimitExceeded or RetentionErrorCode.OwnershipMismatch)) return RetentionCaptureMutationDisposition.Conflict; using var c = OpenExisting(); using var t = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "UPDATE retention_file_capture_reservations SET error_code=$code,updated_at=$at WHERE capture_id=$id AND owner_token=$token AND phase <> 'complete';"; q.Parameters.AddWithValue("$code", code == RetentionErrorCode.CaptureIncomplete ? "retention_capture_incomplete" : code == RetentionErrorCode.ItemLimitExceeded ? "retention_item_limit_exceeded" : "retention_ownership_mismatch"); q.Parameters.AddWithValue("$at", Timestamp(timeProvider.GetUtcNow())); q.Parameters.AddWithValue("$id", captureId);q.Parameters.AddWithValue("$token",capability.OwnerToken); var count = q.ExecuteNonQuery(); t.Commit(); return count == 1 ? RetentionCaptureMutationDisposition.Applied : RetentionCaptureMutationDisposition.StaleNoOp;
    }

    private RetentionFileCaptureRecoverySnapshot? LoadSnapshot(string id) { using var c = OpenExisting(); using var t = c.BeginTransaction(); var row = Load(c, t, id); return row is null || row.Phase == RetentionCapturePhase.Complete ? null : Snapshot(row); }
    private static RetentionFileCaptureRecoverySnapshot Snapshot(Row row) => new(row.CaptureId, row.Store, row.ReservedAt, row.Ticks, row.Phase, row.Cursor, row.Error, row.Parent, row.Staging, row.Final, row.Token, row.MarkerSha256, row.ManifestSha256, row.Count, row.Bytes, row.Members);
    internal static Row? Load(SqliteConnection c, SqliteTransaction t, string id) { using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT store_instance_id,store_kind,source_item_id,reserved_at,reserved_at_utc_ticks,policy_id,policy_version,parent_locator,staging_locator,final_locator,owner_token,marker_sha256,manifest_sha256,phase,durable_cursor,error_code,planned_member_count,planned_total_bytes,legacy_v1 FROM retention_file_capture_reservations WHERE capture_id=$id;"; q.Parameters.AddWithValue("$id", id); using var r = q.ExecuteReader(); if (!r.Read()) return null; var store=r.GetString(0);var reserved=r.GetString(3);var ticks=r.GetInt64(4);var parent=r.GetString(7);var legacy=r.GetInt32(18)==1; if(r.GetString(1)!="sensitive_bundle"||r.GetString(2)!=id||r.GetString(5)!="sensitive-bundle-7d"||r.GetInt32(6)!=1||!DateTimeOffset.TryParseExact(reserved,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)||parsed.UtcDateTime.Ticks!=ticks||Path.GetFullPath(parent)!=parent||(!legacy && r.GetString(8)!=Path.Combine(parent,$".{id}.staging"))||(legacy&&r.GetString(8)!=Path.Combine(Path.GetDirectoryName(parent)!,$".{id}.legacy-staging"))||r.GetString(9)!=Path.Combine(parent,id)) throw new RetentionMigrationBlockedException(); var row = new Row(id, store, reserved, ticks, parent, r.GetString(8), r.GetString(9), (byte[])r[10], r.IsDBNull(11) ? null : (byte[])r[11], r.IsDBNull(12) ? null : (byte[])r[12], r.GetString(13) switch { "reserved" => RetentionCapturePhase.Reserved, "staging" => RetentionCapturePhase.Staging, "published_pending_catalog" => RetentionCapturePhase.PublishedPendingCatalog, "complete" => RetentionCapturePhase.Complete, _ => throw new RetentionMigrationBlockedException() }, r.IsDBNull(14) ? null : r.GetInt32(14), r.IsDBNull(15) ? null : r.GetString(15), r.GetInt32(16), r.GetInt64(17), []); r.Close(); var members=LoadMembers(c,t,id); if (members.Count != row.Count || members.Where(x=>x.ByteLength.HasValue).Sum(x=>x.ByteLength!.Value)!=row.Bytes || !members.Select(x=>x.Ordinal).SequenceEqual(Enumerable.Range(0,members.Count)) || !members.All(x=>x.IsValid) || (row.Phase==RetentionCapturePhase.Reserved && (row.MarkerSha256 is not null||row.ManifestSha256 is not null||row.Count!=0||row.Cursor is not null)) || (row.Phase!=RetentionCapturePhase.Reserved && (row.MarkerSha256 is null||row.ManifestSha256 is null||row.Count==0||row.Cursor is <0 or >256||row.Cursor>row.Count)) || members.Count(x=>x.Kind==RetentionFileCaptureMemberKind.OwnerMarker&&x.RelativePath==RetentionFileCaptureContracts.OwnerMarkerName)!=1 && row.Phase!=RetentionCapturePhase.Reserved || members.Count(x=>x.Kind==RetentionFileCaptureMemberKind.File&&x.RelativePath=="manifest.json"&&row.ManifestSha256 is not null&&RetentionOwnershipReceipt.Matches(x.Sha256!,row.ManifestSha256))!=1 && row.Phase!=RetentionCapturePhase.Reserved || !members.Select(x=>x.DeletionOrder).Order().SequenceEqual(Enumerable.Range(0,members.Count))) throw new RetentionMigrationBlockedException(); return row with { Members=members }; }
    private static IReadOnlyList<RetentionFileCaptureMember> LoadMembers(SqliteConnection c, SqliteTransaction t,string id){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT ordinal,relative_path,member_kind,byte_length,sha256,deletion_order FROM retention_file_capture_members WHERE capture_id=$id ORDER BY ordinal;";q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();var result=new List<RetentionFileCaptureMember>();while(r.Read())result.Add(new(r.GetInt32(0),r.GetString(1),r.GetString(2) switch{"file"=>RetentionFileCaptureMemberKind.File,"directory"=>RetentionFileCaptureMemberKind.Directory,"owner_marker"=>RetentionFileCaptureMemberKind.OwnerMarker,_=>throw new RetentionMigrationBlockedException()},r.IsDBNull(3)?null:r.GetInt64(3),r.IsDBNull(4)?null:(byte[])r[4],r.GetInt32(5)));return result;}
    private static void FileCaptureExec(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object Value)[] ps) { using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = sql; foreach (var (n, v) in ps) q.Parameters.AddWithValue(n, v); q.ExecuteNonQuery(); }
    private static bool CanonicalId(string? id) => id is { Length: 32 } && id.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string MemberKind(RetentionFileCaptureMemberKind kind) => kind switch { RetentionFileCaptureMemberKind.File => "file", RetentionFileCaptureMemberKind.Directory => "directory", _ => "owner_marker" };
    internal sealed record Row(string CaptureId, string Store, string ReservedAt, long Ticks, string Parent, string Staging, string Final, byte[] Token, byte[]? MarkerSha256, byte[]? ManifestSha256, RetentionCapturePhase Phase, int? Cursor, string? Error, int Count,long Bytes,IReadOnlyList<RetentionFileCaptureMember> Members) { internal bool Matches(IRetentionFileCaptureCapability cap) => CaptureId == cap.CaptureId && Store==cap.StoreInstanceId && RetentionOwnershipReceipt.Matches(Token, cap.OwnerToken); }
}

internal enum RetentionCaptureMutationDisposition { Applied, StaleNoOp, NoOpAlreadyFinalized, Conflict }
internal sealed class RetentionFileCapturePlanInput
{
    internal RetentionFileCapturePlanInput(byte[] markerSha256, byte[] manifestSha256, IReadOnlyList<RetentionFileCaptureMember> members) => (MarkerSha256, ManifestSha256, Members) = (markerSha256?.ToArray()!, manifestSha256?.ToArray()!, members?.ToArray()!);
    internal byte[] MarkerSha256 { get; } internal byte[] ManifestSha256 { get; } internal IReadOnlyList<RetentionFileCaptureMember> Members { get; } internal long TotalBytes => Members.Where(static x => x.ByteLength.HasValue).Sum(static x => x.ByteLength!.Value);
    internal bool IsValid => MarkerSha256 is { Length: 32 } && ManifestSha256 is { Length: 32 } && Members.Count is > 0 and <= 256 && Members.Select(static x => x.Ordinal).SequenceEqual(Enumerable.Range(0, Members.Count)) && Members.All(static x => x.IsValid) && Members.Select(static x => x.RelativePath).Distinct(StringComparer.Ordinal).Count() == Members.Count && Members.Select(static x => x.DeletionOrder).Order().SequenceEqual(Enumerable.Range(0, Members.Count)) && Members.Count(x => x.Kind == RetentionFileCaptureMemberKind.OwnerMarker && x.RelativePath == RetentionFileCaptureContracts.OwnerMarkerName) == 1 && Members.Count(x => x.Kind == RetentionFileCaptureMemberKind.File && x.RelativePath == "manifest.json" && x.ByteLength <= 1024 * 1024 && RetentionOwnershipReceipt.Matches(x.Sha256!, ManifestSha256)) == 1 && TotalBytes <= RetentionFileCaptureContracts.MaximumMemberBytes;
    public override string ToString() => GetType().FullName!;
}
internal interface IRetentionFileCaptureCapability { string CaptureId { get; } string StoreInstanceId { get; } byte[] OwnerToken { get; } }
internal sealed class RetentionFileCaptureReservation : IRetentionFileCaptureCapability
{
    internal RetentionFileCaptureReservation(string id, string store, RetentionCapturePhase phase, string parent, string staging, string final, byte[] token, string reservedAt, long ticks, byte[]? marker, byte[]? manifest, IReadOnlyList<RetentionFileCaptureMember> members) => (CaptureId, StoreInstanceId, Phase, ParentLocator, StagingLocator, FinalLocator, OwnerToken, ReservedAtText, ReservedAtTicks, MarkerSha256, ManifestSha256, Members) = (id, store, phase, parent, staging, final, token.ToArray(), reservedAt, ticks, marker?.ToArray(), manifest?.ToArray(), members.ToArray());
    internal string CaptureId { get; } internal string StoreInstanceId { get; } internal RetentionCapturePhase Phase { get; } internal string ParentLocator { get; } internal string StagingLocator { get; } internal string FinalLocator { get; } internal byte[] OwnerToken { get; } internal string ReservedAtText { get; } internal long ReservedAtTicks { get; } internal byte[]? MarkerSha256 { get; } internal byte[]? ManifestSha256 { get; } internal IReadOnlyList<RetentionFileCaptureMember> Members { get; } public override string ToString() => GetType().FullName!;
    string IRetentionFileCaptureCapability.CaptureId => CaptureId; string IRetentionFileCaptureCapability.StoreInstanceId => StoreInstanceId; byte[] IRetentionFileCaptureCapability.OwnerToken => OwnerToken;
}
internal sealed class RetentionFileCaptureRecoverySnapshot : IRetentionFileCaptureCapability
{
    internal RetentionFileCaptureRecoverySnapshot(string id,string store,string reserved,long ticks, RetentionCapturePhase phase, int? cursor, string? error, string parent, string staging, string final, byte[] token, byte[]? marker, byte[]? manifest,int count,long bytes,IReadOnlyList<RetentionFileCaptureMember> members) => (CaptureId,StoreInstanceId,ReservedAtText,ReservedAtTicks, Phase, DurableCursor, ErrorCode, ParentLocator, StagingLocator, FinalLocator, OwnerToken, MarkerSha256, ManifestSha256,PlannedMemberCount,PlannedTotalBytes,Members) = (id,store,reserved,ticks,phase, cursor, error, parent, staging, final, token.ToArray(), marker?.ToArray(), manifest?.ToArray(),count,bytes,members.ToArray());
    internal string CaptureId { get; } internal string StoreInstanceId {get;} internal string ReservedAtText{get;} internal long ReservedAtTicks{get;} internal RetentionCapturePhase Phase { get; } internal int? DurableCursor { get; } internal string? ErrorCode { get; } internal string ParentLocator { get; } internal string StagingLocator { get; } internal string FinalLocator { get; } internal byte[] OwnerToken { get; } internal byte[]? MarkerSha256 { get; } internal byte[]? ManifestSha256 { get; } internal int PlannedMemberCount{get;} internal long PlannedTotalBytes{get;} internal IReadOnlyList<RetentionFileCaptureMember> Members{get;} public override string ToString() => GetType().FullName!;
    string IRetentionFileCaptureCapability.CaptureId => CaptureId; string IRetentionFileCaptureCapability.StoreInstanceId => StoreInstanceId; byte[] IRetentionFileCaptureCapability.OwnerToken => OwnerToken;
}

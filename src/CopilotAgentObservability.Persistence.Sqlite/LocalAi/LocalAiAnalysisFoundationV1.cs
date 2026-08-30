using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.LocalAi;

internal enum LocalAiRunStateV1
{
    Queued, Running, Succeeded, ZeroFindings, ProviderFailed, ProviderPartial,
    InvalidResult, InvalidEvidence, StaleSnapshot, ScopeTooLarge, TimedOut, Canceled,
}

internal enum LocalAiResultValidationCodeV1 { Valid, TooLarge, InvalidResult, InvalidEvidence }

internal sealed record LocalAiResultValidationV1(LocalAiResultValidationCodeV1 Code, byte[]? CanonicalBytes = null);
internal sealed record LocalAiSnapshotV1(string SnapshotId, string ScopeKind, string SessionId, string? NodeId, string AnchorId, byte[] PayloadCanonicalJson, byte[] EvidenceIndexCanonicalJson);
internal sealed record LocalAiRunV1(string RunId, int TimeoutSeconds);
internal sealed record LocalAiReportV1(string RunId, string ResultId, LocalAiRunStateV1 State, DateTimeOffset CreatedAt, byte[] CanonicalResult, string Sha256);
internal sealed record LocalAiReportPageV1(IReadOnlyList<LocalAiReportV1> Items, string? NextCursor);

internal static class LocalAiAnalysisSchemaV1
{
    internal const string ComponentName = "local_ai_analysis";
    internal const int Version = 1;

    internal static void Ensure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        var version = ReadVersion(connection, transaction);
        var ownedCount = OwnedCount(connection, transaction);
        if (version is not null || ownedCount != 0)
        {
            if (version != Version || ownedCount != 3) Reject();
            transaction.Commit();
            return;
        }
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        Execute(connection, transaction, """
            CREATE TABLE local_ai_snapshots(
              snapshot_id TEXT PRIMARY KEY,scope_kind TEXT NOT NULL CHECK(scope_kind IN ('session','node')),
              session_id TEXT NOT NULL,node_id TEXT,anchor_id TEXT NOT NULL,
              payload_json BLOB NOT NULL,payload_sha256 TEXT NOT NULL,evidence_index_json BLOB NOT NULL,evidence_index_sha256 TEXT NOT NULL,
              created_at TEXT NOT NULL,
              CHECK((scope_kind='session' AND node_id IS NULL) OR (scope_kind='node' AND node_id IS NOT NULL)));
            """);
        Execute(connection, transaction, """
            CREATE TABLE local_ai_runs(
              run_id TEXT PRIMARY KEY,snapshot_id TEXT NOT NULL REFERENCES local_ai_snapshots(snapshot_id),scope_kind TEXT NOT NULL,
              session_id TEXT NOT NULL,node_id TEXT,state TEXT NOT NULL CHECK(state IN ('queued','running','succeeded','zero_findings','provider_failed','provider_partial','invalid_result','invalid_evidence','stale_snapshot','scope_too_large','timed_out','canceled')),
              timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 600),created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            """);
        Execute(connection, transaction, """
            CREATE TABLE local_ai_results(
              result_id TEXT PRIMARY KEY,run_id TEXT NOT NULL UNIQUE REFERENCES local_ai_runs(run_id),result_json BLOB NOT NULL,result_sha256 TEXT NOT NULL,created_at TEXT NOT NULL);
            """);
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('local_ai_analysis',1);");
        transaction.Commit();
    }

    private static long? ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!Exists(connection, transaction, "schema_version")) return null;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT component,version,typeof(version) FROM schema_version WHERE component='local_ai_analysis' COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != ComponentName || reader.GetString(2) != "integer") Reject();
        var result = reader.GetInt64(1); if (reader.Read()) Reject(); return result;
    }

    private static long OwnedCount(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND lower(name) LIKE 'local_ai_%';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static bool Exists(SqliteConnection c, SqliteTransaction t, string name) { using var q = c.CreateCommand(); q.Transaction=t; q.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);"; q.Parameters.AddWithValue("$name",name); return Convert.ToInt64(q.ExecuteScalar()) != 0; }
    private static void Execute(SqliteConnection c, SqliteTransaction t, string sql) { using var q=c.CreateCommand(); q.Transaction=t; q.CommandText=sql; q.ExecuteNonQuery(); }
    private static void Reject() => throw new InvalidOperationException("Unsupported incomplete local_ai_analysis schema version 1.");
}

internal static class LocalAiResultValidatorV1
{
    private static readonly string[] Root = ["scope", "snapshot", "summary", "findings", "improvement_suggestions", "limitations", "provenance"];
    private static readonly string[] Finding = ["finding_id", "title", "explanation", "evidence_state", "evidence_refs", "limitation"];
    private static readonly string[] Suggestion = ["suggestion_id", "target_kind", "target_label", "concrete_change", "rationale", "expected_effect", "risks_or_limitations", "evidence_refs"];
    private static readonly HashSet<string> TargetKinds = ["instructions", "skill", "agent", "subagent_input", "tool_configuration"];

    internal static LocalAiResultValidationV1 Validate(ReadOnlySpan<byte> utf8, IReadOnlyCollection<string> evidenceIndex)
    {
        if (utf8.Length > 1_048_576) return new(LocalAiResultValidationCodeV1.TooLarge);
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Exact(root, Root)
                || root.GetProperty("scope").ValueKind != JsonValueKind.Object || root.GetProperty("snapshot").ValueKind != JsonValueKind.Object
                || root.GetProperty("summary").ValueKind != JsonValueKind.String || root.GetProperty("findings").ValueKind != JsonValueKind.Array
                || root.GetProperty("improvement_suggestions").ValueKind != JsonValueKind.Array || root.GetProperty("limitations").ValueKind != JsonValueKind.Array
                || root.GetProperty("provenance").ValueKind != JsonValueKind.Object) return Invalid();
            foreach (var finding in root.GetProperty("findings").EnumerateArray())
            {
                if (!Exact(finding, Finding) || !Strings(finding, "finding_id", "title", "explanation", "limitation")) return Invalid();
                var state = finding.GetProperty("evidence_state");
                if (state.ValueKind != JsonValueKind.String || state.GetString() is not ("supported" or "limited")) return Invalid();
                var refs = ValidateRefs(finding.GetProperty("evidence_refs"), evidenceIndex); if (refs != LocalAiResultValidationCodeV1.Valid) return new(refs);
            }
            foreach (var suggestion in root.GetProperty("improvement_suggestions").EnumerateArray())
            {
                if (!Exact(suggestion, Suggestion) || !Strings(suggestion, "suggestion_id", "target_kind", "target_label", "concrete_change", "rationale", "expected_effect", "risks_or_limitations")) return Invalid();
                if (!TargetKinds.Contains(suggestion.GetProperty("target_kind").GetString()!)) return Invalid();
                var refs = ValidateRefs(suggestion.GetProperty("evidence_refs"), evidenceIndex); if (refs != LocalAiResultValidationCodeV1.Valid) return new(refs);
            }
            foreach (var limitation in root.GetProperty("limitations").EnumerateArray()) if (limitation.ValueKind != JsonValueKind.String) return Invalid();
            var canonical = JsonSerializer.SerializeToUtf8Bytes(root);
            return new(LocalAiResultValidationCodeV1.Valid, canonical);
        }
        catch (JsonException) { return Invalid(); }
        catch (InvalidOperationException) { return Invalid(); }
    }

    private static LocalAiResultValidationCodeV1 ValidateRefs(JsonElement value, IReadOnlyCollection<string> evidence)
    {
        if (value.ValueKind != JsonValueKind.Array) return LocalAiResultValidationCodeV1.InvalidResult;
        var refs = value.EnumerateArray().ToArray();
        if (refs.Length is < 1 or > 16 || refs.Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))) return LocalAiResultValidationCodeV1.InvalidEvidence;
        return refs.All(item => evidence.Contains(item.GetString()!, StringComparer.Ordinal)) ? LocalAiResultValidationCodeV1.Valid : LocalAiResultValidationCodeV1.InvalidEvidence;
    }
    private static bool Exact(JsonElement element, string[] expected) => element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(expected.Order());
    private static bool Strings(JsonElement element, params string[] names) => names.All(name => element.GetProperty(name).ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetProperty(name).GetString()));
    private static LocalAiResultValidationV1 Invalid() => new(LocalAiResultValidationCodeV1.InvalidResult);
}

internal sealed class LocalAiAnalysisStoreV1
{
    private readonly string databasePath;
    internal LocalAiAnalysisStoreV1(string databasePath) { ArgumentException.ThrowIfNullOrWhiteSpace(databasePath); this.databasePath = databasePath; }

    internal void InsertSnapshot(LocalAiSnapshotV1 snapshot)
    {
        ValidateUuid7(snapshot.SnapshotId); ValidateScope(snapshot.ScopeKind, snapshot.SessionId, snapshot.NodeId);
        var payload = Canonical(snapshot.PayloadCanonicalJson); var evidence = Canonical(snapshot.EvidenceIndexCanonicalJson);
        if (!payload.SequenceEqual(snapshot.PayloadCanonicalJson) || !evidence.SequenceEqual(snapshot.EvidenceIndexCanonicalJson)) throw new InvalidOperationException("local_ai_snapshot_not_canonical");
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_ai_snapshots(snapshot_id,scope_kind,session_id,node_id,anchor_id,payload_json,payload_sha256,evidence_index_json,evidence_index_sha256,created_at)
            VALUES($id,$scope,$session,$node,$anchor,$payload,$payloadHash,$evidence,$evidenceHash,$created) ON CONFLICT(snapshot_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", snapshot.SnapshotId); command.Parameters.AddWithValue("$scope", snapshot.ScopeKind); command.Parameters.AddWithValue("$session", snapshot.SessionId);
        command.Parameters.AddWithValue("$node", (object?)snapshot.NodeId ?? DBNull.Value); command.Parameters.AddWithValue("$anchor", snapshot.AnchorId);
        command.Parameters.Add("$payload", SqliteType.Blob).Value=payload; command.Parameters.AddWithValue("$payloadHash", Hash(payload)); command.Parameters.Add("$evidence",SqliteType.Blob).Value=evidence;
        command.Parameters.AddWithValue("$evidenceHash",Hash(evidence)); command.Parameters.AddWithValue("$created",Now());
        if (command.ExecuteNonQuery() == 1) return;
        using var read=connection.CreateCommand(); read.CommandText="SELECT scope_kind,session_id,node_id,anchor_id,payload_json,evidence_index_json FROM local_ai_snapshots WHERE snapshot_id=$id;"; read.Parameters.AddWithValue("$id",snapshot.SnapshotId);
        using var reader=read.ExecuteReader(); if (!reader.Read() || reader.GetString(0)!=snapshot.ScopeKind || reader.GetString(1)!=snapshot.SessionId || (reader.IsDBNull(2)?null:reader.GetString(2))!=snapshot.NodeId || reader.GetString(3)!=snapshot.AnchorId || !((byte[])reader[4]).SequenceEqual(payload) || !((byte[])reader[5]).SequenceEqual(evidence)) throw new InvalidOperationException("local_ai_snapshot_conflict");
    }

    internal LocalAiRunV1 CreateRun(string snapshotId, string scopeKind, string sessionId, string? nodeId, int? timeoutSeconds)
    {
        ValidateUuid7(snapshotId); ValidateScope(scopeKind, sessionId, nodeId); var timeout=timeoutSeconds ?? 60; if (timeout is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        var runId=Guid.CreateVersion7().ToString(); var now=Now(); using var connection=Open();
        using (var snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = "SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id=$id AND scope_kind=$scope AND session_id=$session AND node_id IS $node;";
            snapshot.Parameters.AddWithValue("$id", snapshotId); snapshot.Parameters.AddWithValue("$scope", scopeKind); snapshot.Parameters.AddWithValue("$session", sessionId); snapshot.Parameters.AddWithValue("$node", (object?)nodeId ?? DBNull.Value);
            if (Convert.ToInt64(snapshot.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("local_ai_snapshot_scope_mismatch");
        }
        using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO local_ai_runs(run_id,snapshot_id,scope_kind,session_id,node_id,state,timeout_seconds,created_at,updated_at) VALUES($run,$snapshot,$scope,$session,$node,'queued',$timeout,$now,$now);";
        command.Parameters.AddWithValue("$run",runId); command.Parameters.AddWithValue("$snapshot",snapshotId); command.Parameters.AddWithValue("$scope",scopeKind); command.Parameters.AddWithValue("$session",sessionId); command.Parameters.AddWithValue("$node",(object?)nodeId??DBNull.Value); command.Parameters.AddWithValue("$timeout",timeout); command.Parameters.AddWithValue("$now",now); command.ExecuteNonQuery(); return new(runId,timeout);
    }

    internal void TransitionRun(string runId, LocalAiRunStateV1 next)
    {
        using var connection=Open(); var current=ReadState(connection,runId); if (!Allowed(current,next)) throw new InvalidOperationException("local_ai_run_transition_invalid");
        using var command=connection.CreateCommand(); command.CommandText="UPDATE local_ai_runs SET state=$state,updated_at=$now WHERE run_id=$id AND state=$current;"; command.Parameters.AddWithValue("$state",Wire(next)); command.Parameters.AddWithValue("$now",Now()); command.Parameters.AddWithValue("$id",runId); command.Parameters.AddWithValue("$current",Wire(current)); if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("local_ai_run_transition_conflict");
    }

    internal LocalAiRunStateV1 Complete(string runId, byte[] result)
    {
        using var connection=Open(); if(ReadState(connection,runId)!=LocalAiRunStateV1.Running) throw new InvalidOperationException("local_ai_run_not_running");
        using var evidenceCommand=connection.CreateCommand(); evidenceCommand.CommandText="SELECT s.evidence_index_json FROM local_ai_runs r JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id WHERE r.run_id=$id;"; evidenceCommand.Parameters.AddWithValue("$id",runId);
        var refs=ReadEvidence((byte[])evidenceCommand.ExecuteScalar()!); var validation=LocalAiResultValidatorV1.Validate(result,refs);
        if(validation.Code!=LocalAiResultValidationCodeV1.Valid) { var failed=validation.Code==LocalAiResultValidationCodeV1.InvalidEvidence?LocalAiRunStateV1.InvalidEvidence:LocalAiRunStateV1.InvalidResult; TransitionRun(runId,failed); return failed; }
        var canonical=validation.CanonicalBytes!; var root=JsonDocument.Parse(canonical).RootElement; var state=root.GetProperty("findings").GetArrayLength()==0?LocalAiRunStateV1.ZeroFindings:LocalAiRunStateV1.Succeeded;
        using var transaction=connection.BeginTransaction(); using var insert=connection.CreateCommand(); insert.Transaction=transaction; var resultId=Guid.CreateVersion7().ToString(); var now=Now();
        insert.CommandText="INSERT INTO local_ai_results(result_id,run_id,result_json,result_sha256,created_at) VALUES($result,$run,$json,$hash,$now);"; insert.Parameters.AddWithValue("$result",resultId); insert.Parameters.AddWithValue("$run",runId); insert.Parameters.Add("$json",SqliteType.Blob).Value=canonical; insert.Parameters.AddWithValue("$hash",Hash(canonical)); insert.Parameters.AddWithValue("$now",now); insert.ExecuteNonQuery();
        using var update=connection.CreateCommand(); update.Transaction=transaction; update.CommandText="UPDATE local_ai_runs SET state=$state,updated_at=$now WHERE run_id=$run AND state='running';"; update.Parameters.AddWithValue("$state",Wire(state)); update.Parameters.AddWithValue("$now",now); update.Parameters.AddWithValue("$run",runId); if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("local_ai_run_transition_conflict"); transaction.Commit(); return state;
    }

    internal LocalAiReportPageV1 GetSessionReports(string sessionId, int? limit, string? cursor)
    {
        var take=Math.Min(limit??20,100); if(take<1) throw new ArgumentOutOfRangeException(nameof(limit)); var cursorValue=DecodeCursor(cursor);
        using var connection=Open(); using var command=connection.CreateCommand(); command.CommandText="""
            SELECT r.run_id,x.result_id,r.state,x.created_at,x.result_json,x.result_sha256
            FROM local_ai_runs r JOIN local_ai_results x ON x.run_id=r.run_id
            WHERE r.scope_kind='session' AND r.session_id=$session AND r.state IN ('succeeded','zero_findings')
              AND ($cursor IS NULL OR (x.created_at || '|' || r.run_id) < $cursor)
            ORDER BY x.created_at DESC,r.run_id DESC LIMIT $limit;
            """; command.Parameters.AddWithValue("$session",sessionId); command.Parameters.AddWithValue("$cursor",(object?)cursorValue??DBNull.Value); command.Parameters.AddWithValue("$limit",take+1);
        using var reader=command.ExecuteReader(); var rows=new List<LocalAiReportV1>(); while(reader.Read()) rows.Add(new(reader.GetString(0),reader.GetString(1),Parse(reader.GetString(2)),DateTimeOffset.Parse(reader.GetString(3),CultureInfo.InvariantCulture), (byte[])reader[4],reader.GetString(5)));
        var hasMore=rows.Count>take; if(hasMore) rows.RemoveAt(rows.Count-1); var next=hasMore?EncodeCursor(rows[^1].CreatedAt.ToString("O",CultureInfo.InvariantCulture)+"|"+rows[^1].RunId):null; return new(rows,next);
    }

    private SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Pooling=false}.ToString());c.Open();return c;}
    private static byte[] Canonical(byte[] bytes){using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});return JsonSerializer.SerializeToUtf8Bytes(document.RootElement);}
    private static IReadOnlyCollection<string> ReadEvidence(byte[] bytes){using var doc=JsonDocument.Parse(bytes); if(doc.RootElement.ValueKind!=JsonValueKind.Object || doc.RootElement.EnumerateObject().Select(x=>x.Name).SingleOrDefault()!="evidence_refs") throw new InvalidOperationException("local_ai_evidence_index_invalid"); return doc.RootElement.GetProperty("evidence_refs").EnumerateArray().Select(x=>x.GetString()??throw new InvalidOperationException("local_ai_evidence_index_invalid")).ToHashSet(StringComparer.Ordinal);}
    private static void ValidateScope(string kind,string session,string? node){if(kind is not ("session" or "node") || string.IsNullOrWhiteSpace(session) || (kind=="session")!=(node is null)) throw new ArgumentException("local_ai_scope_invalid");}
    private static void ValidateUuid7(string value){if(!Guid.TryParse(value,out var id)||id.Version!=7) throw new ArgumentException("local_ai_uuid7_required");}
    private static bool Allowed(LocalAiRunStateV1 current,LocalAiRunStateV1 next)=>current==LocalAiRunStateV1.Queued&&next==LocalAiRunStateV1.Running || current==LocalAiRunStateV1.Running&&next is LocalAiRunStateV1.ProviderFailed or LocalAiRunStateV1.ProviderPartial or LocalAiRunStateV1.InvalidResult or LocalAiRunStateV1.InvalidEvidence or LocalAiRunStateV1.StaleSnapshot or LocalAiRunStateV1.ScopeTooLarge or LocalAiRunStateV1.TimedOut or LocalAiRunStateV1.Canceled;
    private static LocalAiRunStateV1 ReadState(SqliteConnection c,string id){using var q=c.CreateCommand();q.CommandText="SELECT state FROM local_ai_runs WHERE run_id=$id;";q.Parameters.AddWithValue("$id",id);return Parse(q.ExecuteScalar() as string??throw new InvalidOperationException("local_ai_run_missing"));}
    private static string Wire(LocalAiRunStateV1 value)=>value switch{LocalAiRunStateV1.Queued=>"queued",LocalAiRunStateV1.Running=>"running",LocalAiRunStateV1.Succeeded=>"succeeded",LocalAiRunStateV1.ZeroFindings=>"zero_findings",LocalAiRunStateV1.ProviderFailed=>"provider_failed",LocalAiRunStateV1.ProviderPartial=>"provider_partial",LocalAiRunStateV1.InvalidResult=>"invalid_result",LocalAiRunStateV1.InvalidEvidence=>"invalid_evidence",LocalAiRunStateV1.StaleSnapshot=>"stale_snapshot",LocalAiRunStateV1.ScopeTooLarge=>"scope_too_large",LocalAiRunStateV1.TimedOut=>"timed_out",LocalAiRunStateV1.Canceled=>"canceled",_=>throw new ArgumentOutOfRangeException(nameof(value))};
    private static LocalAiRunStateV1 Parse(string value)=>value switch{"queued"=>LocalAiRunStateV1.Queued,"running"=>LocalAiRunStateV1.Running,"succeeded"=>LocalAiRunStateV1.Succeeded,"zero_findings"=>LocalAiRunStateV1.ZeroFindings,"provider_failed"=>LocalAiRunStateV1.ProviderFailed,"provider_partial"=>LocalAiRunStateV1.ProviderPartial,"invalid_result"=>LocalAiRunStateV1.InvalidResult,"invalid_evidence"=>LocalAiRunStateV1.InvalidEvidence,"stale_snapshot"=>LocalAiRunStateV1.StaleSnapshot,"scope_too_large"=>LocalAiRunStateV1.ScopeTooLarge,"timed_out"=>LocalAiRunStateV1.TimedOut,"canceled"=>LocalAiRunStateV1.Canceled,_=>throw new InvalidOperationException("local_ai_state_invalid")};
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes)); private static string Now()=>DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    private static string EncodeCursor(string value)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); private static string? DecodeCursor(string? value)=>value is null?null:Encoding.UTF8.GetString(Convert.FromBase64String(value));
}

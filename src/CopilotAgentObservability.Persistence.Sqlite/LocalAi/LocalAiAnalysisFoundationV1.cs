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
internal sealed record LocalAiRunRequestV1(string SnapshotId, string ScopeKind, string SessionId, string? NodeId, string Provider, string Model, string ConfigurationSha256, string PromptTemplateVersion, DateTimeOffset RequestedAt, int? TimeoutSeconds);
internal sealed record LocalAiRunV1(string RunId, int TimeoutSeconds);
internal sealed record LocalAiReportV1(string RunId, string ResultId, LocalAiRunStateV1 State, DateTimeOffset CreatedAt, byte[] CanonicalResult, string Sha256);
internal sealed record LocalAiReportPageV1(IReadOnlyList<LocalAiReportV1> Items, string? NextCursor);

internal static class LocalAiAnalysisSchemaV1
{
    internal const string ComponentName = "local_ai_analysis";
    internal const int Version = 1;
    private static readonly string[] Definitions =
    [
        """CREATE TABLE local_ai_snapshots(snapshot_id TEXT PRIMARY KEY,scope_kind TEXT NOT NULL CHECK(scope_kind IN ('session','node')),session_id TEXT NOT NULL,node_id TEXT,anchor_id TEXT NOT NULL,payload_json BLOB NOT NULL,payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256)=64 AND payload_sha256=lower(payload_sha256)),evidence_index_json BLOB NOT NULL,evidence_index_sha256 TEXT NOT NULL CHECK(length(evidence_index_sha256)=64 AND evidence_index_sha256=lower(evidence_index_sha256)),created_at TEXT NOT NULL,CHECK((scope_kind='session' AND node_id IS NULL) OR (scope_kind='node' AND node_id IS NOT NULL)))""",
        """CREATE TABLE local_ai_runs(run_id TEXT PRIMARY KEY,snapshot_id TEXT NOT NULL REFERENCES local_ai_snapshots(snapshot_id),scope_kind TEXT NOT NULL,session_id TEXT NOT NULL,node_id TEXT,state TEXT NOT NULL CHECK(state IN ('queued','running','succeeded','zero_findings','provider_failed','provider_partial','invalid_result','invalid_evidence','stale_snapshot','scope_too_large','timed_out','canceled')),provider TEXT NOT NULL,model TEXT NOT NULL,configuration_sha256 TEXT NOT NULL CHECK(length(configuration_sha256)=64 AND configuration_sha256=lower(configuration_sha256)),prompt_template_version TEXT NOT NULL,requested_at TEXT NOT NULL,started_at TEXT,completed_at TEXT,timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 600),error_code TEXT,result_id TEXT UNIQUE,created_at TEXT NOT NULL,updated_at TEXT NOT NULL)""",
        """CREATE TABLE local_ai_results(result_id TEXT PRIMARY KEY,run_id TEXT NOT NULL UNIQUE REFERENCES local_ai_runs(run_id),result_json BLOB NOT NULL,result_sha256 TEXT NOT NULL CHECK(length(result_sha256)=64 AND result_sha256=lower(result_sha256)),created_at TEXT NOT NULL)""",
    ];
    private static readonly string[] AdditionalDefinitions =
    [
        "CREATE INDEX IX_local_ai_session_reports ON local_ai_runs(scope_kind,session_id,state,completed_at DESC,run_id DESC)",
        "CREATE TRIGGER local_ai_snapshots_update_rejected BEFORE UPDATE ON local_ai_snapshots BEGIN SELECT RAISE(ABORT,'local_ai_snapshot_immutable'); END",
        "CREATE TRIGGER local_ai_results_update_rejected BEFORE UPDATE ON local_ai_results BEGIN SELECT RAISE(ABORT,'local_ai_result_immutable'); END",
        "CREATE TRIGGER local_ai_terminal_run_update_rejected BEFORE UPDATE ON local_ai_runs WHEN OLD.state NOT IN ('queued','running') BEGIN SELECT RAISE(ABORT,'local_ai_terminal_run_immutable'); END",
    ];

    internal static void Ensure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        var version = ReadVersion(connection, transaction);
        var ownedCount = OwnedCount(connection, transaction);
        if (version is not null || ownedCount != 0)
        {
            if (version != Version || ownedCount != Definitions.Length + AdditionalDefinitions.Length || !HasExactSchema(connection, transaction)) Reject();
            transaction.Commit();
            return;
        }
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        foreach (var definition in Definitions) Execute(connection, transaction, definition + ";");
        foreach (var definition in AdditionalDefinitions) Execute(connection, transaction, definition + ";");
        if (!HasExactSchema(connection, transaction)) Reject();
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
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE lower(name) LIKE 'local_ai_%' OR lower(name) LIKE 'ix_local_ai_%';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static bool HasExactSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name LIKE 'local_ai_%' OR name LIKE 'IX_local_ai_%' ORDER BY name;";
        using var reader = command.ExecuteReader(); var actual = new List<string>(); while (reader.Read()) actual.Add(Normalize(reader.GetString(0)));
        return actual.Order(StringComparer.Ordinal).SequenceEqual(Definitions.Concat(AdditionalDefinitions).Select(Normalize).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }
    private static string Normalize(string sql)
    {
        var normalized=new StringBuilder(sql.Length); var inLiteral=false;
        for(var index=0;index<sql.Length;index++)
        {
            var character=sql[index];
            if(character=='\'' && inLiteral && index+1<sql.Length && sql[index+1]=='\'') { normalized.Append("''"); index++; continue; }
            if(character=='\'') { inLiteral=!inLiteral; normalized.Append(character); continue; }
            if(inLiteral || !char.IsWhiteSpace(character)) normalized.Append(character);
        }
        return normalized.ToString().TrimEnd(';');
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
    private static readonly string[] Scope = ["kind", "session_id", "node_id", "anchor_id"];
    private static readonly string[] Snapshot = ["snapshot_id", "payload_sha256"];
    private static readonly string[] Provenance = ["provider", "model", "configuration_sha256", "prompt_template_version", "requested_at", "started_at", "completed_at", "snapshot_id", "snapshot_sha256", "coverage"];
    private static readonly string[] Coverage = ["included", "excluded", "content_available"];
    private static readonly HashSet<string> TargetKinds = ["instructions", "skill", "agent", "subagent_input", "tool_configuration"];

    internal static LocalAiResultValidationV1 Validate(ReadOnlySpan<byte> utf8, IReadOnlyCollection<string> evidenceIndex)
    {
        if (utf8.Length > 1_048_576) return new(LocalAiResultValidationCodeV1.TooLarge);
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Exact(root, Root)
                || !ValidScope(root.GetProperty("scope")) || !ValidSnapshot(root.GetProperty("snapshot")) || !ValidProvenance(root.GetProperty("provenance"))
                || root.GetProperty("summary").ValueKind != JsonValueKind.String || root.GetProperty("findings").ValueKind != JsonValueKind.Array
                || root.GetProperty("improvement_suggestions").ValueKind != JsonValueKind.Array || root.GetProperty("limitations").ValueKind != JsonValueKind.Array
                ) return Invalid();
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
            if (!ValidLimitations(root.GetProperty("limitations"))) return Invalid();
            var canonical = LocalAiCanonicalJsonV1.Serialize(root);
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
    private static bool ValidScope(JsonElement value)
    {
        if (!Exact(value, Scope) || !Strings(value, "kind", "session_id", "anchor_id") || !CanonicalUuid7(value.GetProperty("session_id").GetString()!)) return false;
        var kind = value.GetProperty("kind").GetString(); var node = value.GetProperty("node_id");
        return kind == "session" && node.ValueKind == JsonValueKind.Null || kind == "node" && node.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(node.GetString());
    }
    private static bool ValidSnapshot(JsonElement value) => Exact(value, Snapshot) && Strings(value, "snapshot_id", "payload_sha256") && CanonicalUuid7(value.GetProperty("snapshot_id").GetString()!) && Hash(value.GetProperty("payload_sha256").GetString()!);
    private static bool ValidProvenance(JsonElement value)
    {
        if (!Exact(value, Provenance) || !Strings(value, "provider", "model", "configuration_sha256", "prompt_template_version", "requested_at", "started_at", "completed_at", "snapshot_id", "snapshot_sha256")
            || !Hash(value.GetProperty("configuration_sha256").GetString()!) || !Hash(value.GetProperty("snapshot_sha256").GetString()!) || !CanonicalUuid7(value.GetProperty("snapshot_id").GetString()!)) return false;
        if (!Timestamp(value, "requested_at", out var requested) || !Timestamp(value, "started_at", out var started) || !Timestamp(value, "completed_at", out var completed) || requested > started || started > completed) return false;
        var coverage = value.GetProperty("coverage");
        return Exact(coverage, Coverage) && coverage.GetProperty("included").TryGetInt32(out var included) && included >= 0
            && coverage.GetProperty("excluded").TryGetInt32(out var excluded) && excluded >= 0 && coverage.GetProperty("content_available").ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
    private static bool Timestamp(JsonElement value, string name, out DateTimeOffset timestamp) => DateTimeOffset.TryParseExact(value.GetProperty(name).GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    internal static bool CanonicalUuid7(string value) => value.Length == 36 && value == value.ToLowerInvariant() && Guid.TryParseExact(value, "D", out var id) && id.Version == 7 && (id.ToByteArray()[8] & 0xc0) == 0x80;
    private static bool Hash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidLimitations(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String || ValidLimitations(item));
}

internal static class LocalAiCanonicalJsonV1
{
    internal static byte[] Serialize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false })) Write(writer, value);
        return stream.ToArray();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject(); foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); Write(writer, property.Value); } writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            default: value.WriteTo(writer); break;
        }
    }
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
        ValidateEvidenceIndex(evidence);
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

    internal LocalAiRunV1 CreateRun(LocalAiRunRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request); var snapshotId=request.SnapshotId; var scopeKind=request.ScopeKind; var sessionId=request.SessionId; var nodeId=request.NodeId;
        ValidateUuid7(snapshotId); ValidateScope(scopeKind, sessionId, nodeId); ValidateMetadata(request); var timeout=request.TimeoutSeconds ?? 60; if (timeout is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(request.TimeoutSeconds));
        var runId=Guid.CreateVersion7().ToString(); var now=Now(); using var connection=Open();
        using (var snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = "SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id=$id AND scope_kind=$scope AND session_id=$session AND node_id IS $node;";
            snapshot.Parameters.AddWithValue("$id", snapshotId); snapshot.Parameters.AddWithValue("$scope", scopeKind); snapshot.Parameters.AddWithValue("$session", sessionId); snapshot.Parameters.AddWithValue("$node", (object?)nodeId ?? DBNull.Value);
            if (Convert.ToInt64(snapshot.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("local_ai_snapshot_scope_mismatch");
        }
        using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO local_ai_runs(run_id,snapshot_id,scope_kind,session_id,node_id,state,provider,model,configuration_sha256,prompt_template_version,requested_at,started_at,completed_at,timeout_seconds,error_code,result_id,created_at,updated_at) VALUES($run,$snapshot,$scope,$session,$node,'queued',$provider,$model,$configuration,$template,$requested,NULL,NULL,$timeout,NULL,NULL,$now,$now);";
        command.Parameters.AddWithValue("$run",runId); command.Parameters.AddWithValue("$snapshot",snapshotId); command.Parameters.AddWithValue("$scope",scopeKind); command.Parameters.AddWithValue("$session",sessionId); command.Parameters.AddWithValue("$node",(object?)nodeId??DBNull.Value); command.Parameters.AddWithValue("$provider",request.Provider); command.Parameters.AddWithValue("$model",request.Model); command.Parameters.AddWithValue("$configuration",request.ConfigurationSha256); command.Parameters.AddWithValue("$template",request.PromptTemplateVersion); command.Parameters.AddWithValue("$requested",request.RequestedAt.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$timeout",timeout); command.Parameters.AddWithValue("$now",now); command.ExecuteNonQuery(); return new(runId,timeout);
    }

    internal void TransitionRun(string runId, LocalAiRunStateV1 next, string? errorCode = null, DateTimeOffset? occurredAt = null)
    {
        using var connection=Open(); var current=ReadState(connection,runId); if (!Allowed(current,next)) throw new InvalidOperationException("local_ai_run_transition_invalid");
        if (next == LocalAiRunStateV1.Running && errorCode is not null || next != LocalAiRunStateV1.Running && errorCode != Wire(next)) throw new ArgumentException("local_ai_error_code_invalid");
        var now=(occurredAt??DateTimeOffset.UtcNow).ToUniversalTime().ToString("O",CultureInfo.InvariantCulture); using var command=connection.CreateCommand(); command.CommandText=next==LocalAiRunStateV1.Running
            ? "UPDATE local_ai_runs SET state=$state,started_at=$now,updated_at=$now WHERE run_id=$id AND state=$current AND started_at IS NULL AND completed_at IS NULL;"
            : "UPDATE local_ai_runs SET state=$state,completed_at=$now,error_code=$error,updated_at=$now WHERE run_id=$id AND state=$current AND started_at IS NOT NULL AND completed_at IS NULL AND result_id IS NULL;";
        command.Parameters.AddWithValue("$state",Wire(next)); command.Parameters.AddWithValue("$now",now); command.Parameters.AddWithValue("$id",runId); command.Parameters.AddWithValue("$current",Wire(current)); command.Parameters.AddWithValue("$error",(object?)errorCode??DBNull.Value); if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("local_ai_run_transition_conflict");
    }

    internal LocalAiRunStateV1 Complete(string runId, byte[] result, DateTimeOffset? completedAt = null)
    {
        using var connection=Open(); if(ReadState(connection,runId)!=LocalAiRunStateV1.Running) throw new InvalidOperationException("local_ai_run_not_running");
        using var evidenceCommand=connection.CreateCommand(); evidenceCommand.CommandText="SELECT s.evidence_index_json,s.payload_sha256,s.snapshot_id,s.scope_kind,s.session_id,s.node_id,s.anchor_id,r.provider,r.model,r.configuration_sha256,r.prompt_template_version,r.requested_at,r.started_at FROM local_ai_runs r JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id WHERE r.run_id=$id;"; evidenceCommand.Parameters.AddWithValue("$id",runId);
        using var expectedReader=evidenceCommand.ExecuteReader(); if(!expectedReader.Read()) throw new InvalidOperationException("local_ai_run_missing");
        var completion=(completedAt??DateTimeOffset.UtcNow).ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
        var expected=new ExpectedResult((byte[])expectedReader[0],expectedReader.GetString(1),expectedReader.GetString(2),expectedReader.GetString(3),expectedReader.GetString(4),expectedReader.IsDBNull(5)?null:expectedReader.GetString(5),expectedReader.GetString(6),expectedReader.GetString(7),expectedReader.GetString(8),expectedReader.GetString(9),expectedReader.GetString(10),expectedReader.GetString(11),expectedReader.GetString(12),completion);
        expectedReader.Close(); var refs=ReadEvidence(expected.EvidenceIndex); var validation=LocalAiResultValidatorV1.Validate(result,refs);
        if(validation.Code==LocalAiResultValidationCodeV1.Valid && !MatchesExpected(validation.CanonicalBytes!,expected)) validation=new(LocalAiResultValidationCodeV1.InvalidResult);
        if(validation.Code!=LocalAiResultValidationCodeV1.Valid) { var failed=validation.Code==LocalAiResultValidationCodeV1.InvalidEvidence?LocalAiRunStateV1.InvalidEvidence:LocalAiRunStateV1.InvalidResult; TransitionRun(runId,failed,failed==LocalAiRunStateV1.InvalidEvidence?"invalid_evidence":"invalid_result"); return failed; }
        var canonical=validation.CanonicalBytes!; var root=JsonDocument.Parse(canonical).RootElement; var state=root.GetProperty("findings").GetArrayLength()==0?LocalAiRunStateV1.ZeroFindings:LocalAiRunStateV1.Succeeded;
        using var transaction=connection.BeginTransaction(); using var insert=connection.CreateCommand(); insert.Transaction=transaction; var resultId=Guid.CreateVersion7().ToString(); var now=completion;
        insert.CommandText="INSERT INTO local_ai_results(result_id,run_id,result_json,result_sha256,created_at) VALUES($result,$run,$json,$hash,$now);"; insert.Parameters.AddWithValue("$result",resultId); insert.Parameters.AddWithValue("$run",runId); insert.Parameters.Add("$json",SqliteType.Blob).Value=canonical; insert.Parameters.AddWithValue("$hash",Hash(canonical)); insert.Parameters.AddWithValue("$now",now); insert.ExecuteNonQuery();
        using var update=connection.CreateCommand(); update.Transaction=transaction; update.CommandText="UPDATE local_ai_runs SET state=$state,completed_at=$now,result_id=$result,error_code=NULL,updated_at=$now WHERE run_id=$run AND state='running' AND completed_at IS NULL AND result_id IS NULL;"; update.Parameters.AddWithValue("$state",Wire(state)); update.Parameters.AddWithValue("$now",now); update.Parameters.AddWithValue("$result",resultId); update.Parameters.AddWithValue("$run",runId); if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("local_ai_run_transition_conflict"); transaction.Commit(); return state;
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
    private static byte[] Canonical(byte[] bytes){using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});return LocalAiCanonicalJsonV1.Serialize(document.RootElement);}
    private static IReadOnlyCollection<string> ReadEvidence(byte[] bytes){using var doc=JsonDocument.Parse(bytes); if(doc.RootElement.ValueKind!=JsonValueKind.Object || doc.RootElement.EnumerateObject().Select(x=>x.Name).SingleOrDefault()!="evidence_refs") throw new InvalidOperationException("local_ai_evidence_index_invalid"); return doc.RootElement.GetProperty("evidence_refs").EnumerateArray().Select(x=>x.GetString()??throw new InvalidOperationException("local_ai_evidence_index_invalid")).ToHashSet(StringComparer.Ordinal);}
    private static void ValidateEvidenceIndex(byte[] bytes)
    {
        try
        {
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16}); var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || !root.EnumerateObject().Select(item=>item.Name).SequenceEqual(["evidence_refs"]) || root.GetProperty("evidence_refs").ValueKind!=JsonValueKind.Array) throw new InvalidOperationException();
            var values=root.GetProperty("evidence_refs").EnumerateArray().Select(item=>item.ValueKind==JsonValueKind.String?item.GetString():null).ToArray();
            if(values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count()!=values.Length) throw new InvalidOperationException();
        }
        catch(Exception exception) when(exception is JsonException or InvalidOperationException) { throw new InvalidOperationException("local_ai_evidence_index_invalid"); }
    }
    private static void ValidateMetadata(LocalAiRunRequestV1 request)
    {
        if(request.Provider!="github_copilot_sdk" || !Token(request.Model) || !Token(request.PromptTemplateVersion)
            || request.ConfigurationSha256.Length!=64 || request.ConfigurationSha256.Any(character=>character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("local_ai_run_metadata_invalid");
    }
    private static bool Token(string value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=200&&value.All(character=>char.IsLetterOrDigit(character)||character is '_' or '-' or '.');
    private static void ValidateScope(string kind,string session,string? node){if(kind is not ("session" or "node") || string.IsNullOrWhiteSpace(session) || (kind=="session")!=(node is null)) throw new ArgumentException("local_ai_scope_invalid");}
    private static void ValidateUuid7(string value){if(!LocalAiResultValidatorV1.CanonicalUuid7(value)) throw new ArgumentException("local_ai_uuid7_required");}
    private static bool Allowed(LocalAiRunStateV1 current,LocalAiRunStateV1 next)=>current==LocalAiRunStateV1.Queued&&next==LocalAiRunStateV1.Running || current==LocalAiRunStateV1.Running&&next is LocalAiRunStateV1.ProviderFailed or LocalAiRunStateV1.ProviderPartial or LocalAiRunStateV1.InvalidResult or LocalAiRunStateV1.InvalidEvidence or LocalAiRunStateV1.StaleSnapshot or LocalAiRunStateV1.ScopeTooLarge or LocalAiRunStateV1.TimedOut or LocalAiRunStateV1.Canceled;
    private static LocalAiRunStateV1 ReadState(SqliteConnection c,string id){using var q=c.CreateCommand();q.CommandText="SELECT state FROM local_ai_runs WHERE run_id=$id;";q.Parameters.AddWithValue("$id",id);return Parse(q.ExecuteScalar() as string??throw new InvalidOperationException("local_ai_run_missing"));}
    private static string Wire(LocalAiRunStateV1 value)=>value switch{LocalAiRunStateV1.Queued=>"queued",LocalAiRunStateV1.Running=>"running",LocalAiRunStateV1.Succeeded=>"succeeded",LocalAiRunStateV1.ZeroFindings=>"zero_findings",LocalAiRunStateV1.ProviderFailed=>"provider_failed",LocalAiRunStateV1.ProviderPartial=>"provider_partial",LocalAiRunStateV1.InvalidResult=>"invalid_result",LocalAiRunStateV1.InvalidEvidence=>"invalid_evidence",LocalAiRunStateV1.StaleSnapshot=>"stale_snapshot",LocalAiRunStateV1.ScopeTooLarge=>"scope_too_large",LocalAiRunStateV1.TimedOut=>"timed_out",LocalAiRunStateV1.Canceled=>"canceled",_=>throw new ArgumentOutOfRangeException(nameof(value))};
    private static LocalAiRunStateV1 Parse(string value)=>value switch{"queued"=>LocalAiRunStateV1.Queued,"running"=>LocalAiRunStateV1.Running,"succeeded"=>LocalAiRunStateV1.Succeeded,"zero_findings"=>LocalAiRunStateV1.ZeroFindings,"provider_failed"=>LocalAiRunStateV1.ProviderFailed,"provider_partial"=>LocalAiRunStateV1.ProviderPartial,"invalid_result"=>LocalAiRunStateV1.InvalidResult,"invalid_evidence"=>LocalAiRunStateV1.InvalidEvidence,"stale_snapshot"=>LocalAiRunStateV1.StaleSnapshot,"scope_too_large"=>LocalAiRunStateV1.ScopeTooLarge,"timed_out"=>LocalAiRunStateV1.TimedOut,"canceled"=>LocalAiRunStateV1.Canceled,_=>throw new InvalidOperationException("local_ai_state_invalid")};
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes)); private static string Now()=>DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    private static string EncodeCursor(string value)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); private static string? DecodeCursor(string? value)=>value is null?null:Encoding.UTF8.GetString(Convert.FromBase64String(value));
    private static bool MatchesExpected(byte[] bytes,ExpectedResult expected)
    {
        using var document=JsonDocument.Parse(bytes); var root=document.RootElement; var scope=root.GetProperty("scope"); var snapshot=root.GetProperty("snapshot"); var provenance=root.GetProperty("provenance");
        return scope.GetProperty("kind").GetString()==expected.ScopeKind && scope.GetProperty("session_id").GetString()==expected.SessionId
            && (scope.GetProperty("node_id").ValueKind==JsonValueKind.Null?null:scope.GetProperty("node_id").GetString())==expected.NodeId && scope.GetProperty("anchor_id").GetString()==expected.AnchorId
            && snapshot.GetProperty("snapshot_id").GetString()==expected.SnapshotId && snapshot.GetProperty("payload_sha256").GetString()==expected.PayloadSha256
            && provenance.GetProperty("provider").GetString()==expected.Provider && provenance.GetProperty("model").GetString()==expected.Model
            && provenance.GetProperty("configuration_sha256").GetString()==expected.ConfigurationSha256 && provenance.GetProperty("prompt_template_version").GetString()==expected.Template
            && provenance.GetProperty("requested_at").GetString()==expected.RequestedAt && provenance.GetProperty("snapshot_id").GetString()==expected.SnapshotId
            && provenance.GetProperty("snapshot_sha256").GetString()==expected.PayloadSha256 && provenance.GetProperty("started_at").GetString()==expected.StartedAt
            && provenance.GetProperty("completed_at").GetString()==expected.CompletedAt;
    }
    private sealed record ExpectedResult(byte[] EvidenceIndex,string PayloadSha256,string SnapshotId,string ScopeKind,string SessionId,string? NodeId,string AnchorId,string Provider,string Model,string ConfigurationSha256,string Template,string RequestedAt,string StartedAt,string CompletedAt);
}

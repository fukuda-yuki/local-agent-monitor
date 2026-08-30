using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.LocalAi;

internal enum LocalAiRunStateV1
{
    Queued, Running, Succeeded, ZeroFindings, ProviderFailed, ProviderPartial,
    InvalidResult, InvalidEvidence, StaleSnapshot, ScopeTooLarge, TimedOut, Canceled,
}

internal enum LocalAiResultValidationCodeV1 { Valid, TooLarge, InvalidResult, InvalidEvidence }
internal enum LocalAiScopeKindV1 { Session, Node, RepositorySelection, Comparison }

internal sealed record LocalAiScopeIdentityV1(
    LocalAiScopeKindV1 Kind,
    string? SessionId,
    string? NodeId,
    string? RepositoryId,
    string? ComparisonId)
{
    internal string WireKind => Kind switch
    {
        LocalAiScopeKindV1.Session => "session",
        LocalAiScopeKindV1.Node => "node",
        LocalAiScopeKindV1.RepositorySelection => "repository_selection",
        LocalAiScopeKindV1.Comparison => "comparison",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };

    internal string AnchorId => Kind switch
    {
        LocalAiScopeKindV1.Session => SessionId!,
        LocalAiScopeKindV1.Node => NodeId!,
        LocalAiScopeKindV1.RepositorySelection => RepositoryId!,
        LocalAiScopeKindV1.Comparison => ComparisonId!,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };

    internal static LocalAiScopeIdentityV1 Create(string kind, string? sessionId, string? nodeId,
        string? repositoryId, string? comparisonId)
    {
        LocalAiScopeIdentityV1 identity = kind switch
        {
            "session" => new(LocalAiScopeKindV1.Session, sessionId, nodeId, repositoryId, comparisonId),
            "node" => new(LocalAiScopeKindV1.Node, sessionId, nodeId, repositoryId, comparisonId),
            "repository_selection" => new(LocalAiScopeKindV1.RepositorySelection, sessionId, nodeId, repositoryId, comparisonId),
            "comparison" => new(LocalAiScopeKindV1.Comparison, sessionId, nodeId, repositoryId, comparisonId),
            _ => throw new ArgumentException("local_ai_scope_invalid"),
        };
        var valid = identity.Kind switch
        {
            LocalAiScopeKindV1.Session => Canonical(sessionId) && nodeId is null && repositoryId is null && comparisonId is null,
            LocalAiScopeKindV1.Node => Canonical(sessionId) && !string.IsNullOrWhiteSpace(nodeId) && repositoryId is null && comparisonId is null,
            LocalAiScopeKindV1.RepositorySelection => sessionId is null && nodeId is null && Canonical(repositoryId) && comparisonId is null,
            LocalAiScopeKindV1.Comparison => sessionId is null && nodeId is null && Canonical(repositoryId) && Canonical(comparisonId),
            _ => false,
        };
        return valid ? identity : throw new ArgumentException("local_ai_scope_invalid");
    }

    private static bool Canonical(string? value) => value is not null && LocalAiResultValidatorV1.CanonicalUuid7(value);
}

internal sealed record LocalAiResultValidationV1(LocalAiResultValidationCodeV1 Code, byte[]? CanonicalBytes = null);
internal sealed record LocalAiSnapshotV1(string SnapshotId, string ScopeKind, string? SessionId, string? NodeId, string AnchorId, byte[] PayloadCanonicalJson, byte[] EvidenceIndexCanonicalJson, string? RepositoryId = null, string? ComparisonId = null, DateTimeOffset? ExpiresAt = null);
internal sealed record LocalAiRunRequestV1(string SnapshotId, string ScopeKind, string? SessionId, string? NodeId, string Provider, string Model, string ConfigurationSha256, string PromptTemplateVersion, DateTimeOffset RequestedAt, int? TimeoutSeconds, string? RepositoryId = null, string? ComparisonId = null);
internal sealed record LocalAiRunV1(string RunId, int TimeoutSeconds);
internal sealed record LocalAiReportV1(string RunId, string? ResultId, LocalAiRunStateV1 State, DateTimeOffset CreatedAt, byte[]? CanonicalResult, string? Sha256, string ContentState);
internal sealed record LocalAiReportPageV1(IReadOnlyList<LocalAiReportV1> Items, string? NextCursor);
internal sealed record LocalAiStoredResultInvariantV1(byte[]? EvidenceIndex,string PayloadSha256,string SnapshotId,string ScopeKind,string? SessionId,string? NodeId,string AnchorId,string Provider,string Model,string ConfigurationSha256,string Template,string RequestedAt,string StartedAt,string CompletedAt,string RunState,string? RepositoryId=null,string? ComparisonId=null);
internal sealed record LocalAiStoredSnapshotInvariantV1(string SnapshotId,string ScopeKind,string? SessionId,string? NodeId,string AnchorId,byte[] Payload,string PayloadSha256,byte[] EvidenceIndex,string EvidenceIndexSha256,string? RepositoryId=null,string? ComparisonId=null);

internal static class LocalAiAnalysisSchemaV1
{
    internal const string ComponentName = "local_ai_analysis";
    internal const int Version = 2;
    private static readonly string[] VersionOneDefinitions =
    [
        """CREATE TABLE local_ai_snapshots(snapshot_id TEXT PRIMARY KEY,scope_kind TEXT NOT NULL CHECK(scope_kind IN ('session','node')),session_id TEXT NOT NULL,node_id TEXT,anchor_id TEXT NOT NULL,payload_json BLOB,payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256)=64 AND payload_sha256=lower(payload_sha256)),evidence_index_json BLOB,evidence_index_sha256 TEXT NOT NULL CHECK(length(evidence_index_sha256)=64 AND evidence_index_sha256=lower(evidence_index_sha256)),retention_owner_token BLOB NOT NULL CHECK(length(retention_owner_token)=32),created_at TEXT NOT NULL,CHECK((scope_kind='session' AND node_id IS NULL) OR (scope_kind='node' AND node_id IS NOT NULL)),CHECK((payload_json IS NULL)=(evidence_index_json IS NULL)))""",
        """CREATE TABLE local_ai_runs(run_id TEXT PRIMARY KEY,snapshot_id TEXT NOT NULL REFERENCES local_ai_snapshots(snapshot_id),scope_kind TEXT NOT NULL,session_id TEXT NOT NULL,node_id TEXT,state TEXT NOT NULL CHECK(state IN ('queued','running','succeeded','zero_findings','provider_failed','provider_partial','invalid_result','invalid_evidence','stale_snapshot','scope_too_large','timed_out','canceled')),provider TEXT NOT NULL,model TEXT NOT NULL,configuration_sha256 TEXT NOT NULL CHECK(length(configuration_sha256)=64 AND configuration_sha256=lower(configuration_sha256)),prompt_template_version TEXT NOT NULL,requested_at TEXT NOT NULL,started_at TEXT,completed_at TEXT,timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 600),error_code TEXT,result_id TEXT UNIQUE,created_at TEXT NOT NULL,updated_at TEXT NOT NULL)""",
        """CREATE TABLE local_ai_results(result_id TEXT PRIMARY KEY,run_id TEXT NOT NULL UNIQUE REFERENCES local_ai_runs(run_id),result_json BLOB,result_sha256 TEXT NOT NULL CHECK(length(result_sha256)=64 AND result_sha256=lower(result_sha256)),retention_owner_token BLOB NOT NULL CHECK(length(retention_owner_token)=32),created_at TEXT NOT NULL)""",
    ];
    private static readonly string[] VersionOneAdditionalDefinitions =
    [
        "CREATE INDEX IX_local_ai_session_reports ON local_ai_runs(scope_kind,session_id,state,completed_at DESC,run_id DESC)",
        "CREATE TRIGGER local_ai_snapshots_update_rejected BEFORE UPDATE ON local_ai_snapshots WHEN NOT (local_ai_retention_delete_authorized('snapshot',OLD.snapshot_id)=1 AND OLD.scope_kind='session' AND OLD.payload_json IS NOT NULL AND OLD.evidence_index_json IS NOT NULL AND NEW.payload_json IS NULL AND NEW.evidence_index_json IS NULL AND NEW.snapshot_id=OLD.snapshot_id AND NEW.scope_kind=OLD.scope_kind AND NEW.session_id=OLD.session_id AND NEW.node_id IS OLD.node_id AND NEW.anchor_id=OLD.anchor_id AND NEW.payload_sha256=OLD.payload_sha256 AND NEW.evidence_index_sha256=OLD.evidence_index_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at) BEGIN SELECT RAISE(ABORT,'local_ai_snapshot_immutable'); END",
        "CREATE TRIGGER local_ai_results_update_rejected BEFORE UPDATE ON local_ai_results WHEN NOT (local_ai_retention_delete_authorized('result',OLD.result_id)=1 AND OLD.result_json IS NOT NULL AND NEW.result_json IS NULL AND NEW.result_id=OLD.result_id AND NEW.run_id=OLD.run_id AND NEW.result_sha256=OLD.result_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at) BEGIN SELECT RAISE(ABORT,'local_ai_result_immutable'); END",
        "CREATE TRIGGER local_ai_terminal_run_update_rejected BEFORE UPDATE ON local_ai_runs WHEN OLD.state NOT IN ('queued','running') BEGIN SELECT RAISE(ABORT,'local_ai_terminal_run_immutable'); END",
    ];
    private static readonly string[] Definitions =
    [
        """CREATE TABLE local_ai_snapshots(snapshot_id TEXT PRIMARY KEY,scope_kind TEXT NOT NULL CHECK(scope_kind IN ('session','node','repository_selection','comparison')),session_id TEXT,node_id TEXT,repository_id TEXT,comparison_id TEXT,anchor_id TEXT NOT NULL,payload_json BLOB,payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256)=64 AND payload_sha256=lower(payload_sha256)),evidence_index_json BLOB,evidence_index_sha256 TEXT NOT NULL CHECK(length(evidence_index_sha256)=64 AND evidence_index_sha256=lower(evidence_index_sha256)),retention_owner_token BLOB NOT NULL CHECK(length(retention_owner_token)=32),created_at TEXT NOT NULL,expires_at TEXT,CHECK((scope_kind='session' AND session_id IS NOT NULL AND node_id IS NULL AND repository_id IS NULL AND comparison_id IS NULL AND expires_at IS NULL) OR (scope_kind='node' AND session_id IS NOT NULL AND node_id IS NOT NULL AND repository_id IS NULL AND comparison_id IS NULL AND expires_at IS NOT NULL) OR (scope_kind='repository_selection' AND session_id IS NULL AND node_id IS NULL AND repository_id IS NOT NULL AND comparison_id IS NULL AND expires_at IS NOT NULL) OR (scope_kind='comparison' AND session_id IS NULL AND node_id IS NULL AND repository_id IS NOT NULL AND comparison_id IS NOT NULL AND expires_at IS NOT NULL)),CHECK((payload_json IS NULL)=(evidence_index_json IS NULL)))""",
        """CREATE TABLE local_ai_runs(run_id TEXT PRIMARY KEY,snapshot_id TEXT NOT NULL REFERENCES local_ai_snapshots(snapshot_id),scope_kind TEXT NOT NULL CHECK(scope_kind IN ('session','node','repository_selection','comparison')),session_id TEXT,node_id TEXT,repository_id TEXT,comparison_id TEXT,state TEXT NOT NULL CHECK(state IN ('queued','running','succeeded','zero_findings','provider_failed','provider_partial','invalid_result','invalid_evidence','stale_snapshot','scope_too_large','timed_out','canceled')),provider TEXT NOT NULL,model TEXT NOT NULL,configuration_sha256 TEXT NOT NULL CHECK(length(configuration_sha256)=64 AND configuration_sha256=lower(configuration_sha256)),prompt_template_version TEXT NOT NULL,requested_at TEXT NOT NULL,started_at TEXT,completed_at TEXT,timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 600),error_code TEXT,result_id TEXT UNIQUE,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,expires_at TEXT,CHECK((scope_kind='session' AND session_id IS NOT NULL AND node_id IS NULL AND repository_id IS NULL AND comparison_id IS NULL AND expires_at IS NULL) OR (scope_kind='node' AND session_id IS NOT NULL AND node_id IS NOT NULL AND repository_id IS NULL AND comparison_id IS NULL AND expires_at IS NOT NULL) OR (scope_kind='repository_selection' AND session_id IS NULL AND node_id IS NULL AND repository_id IS NOT NULL AND comparison_id IS NULL AND expires_at IS NOT NULL) OR (scope_kind='comparison' AND session_id IS NULL AND node_id IS NULL AND repository_id IS NOT NULL AND comparison_id IS NOT NULL AND expires_at IS NOT NULL)))""",
        """CREATE TABLE local_ai_results(result_id TEXT PRIMARY KEY,run_id TEXT NOT NULL UNIQUE REFERENCES local_ai_runs(run_id),result_json BLOB,result_sha256 TEXT NOT NULL CHECK(length(result_sha256)=64 AND result_sha256=lower(result_sha256)),retention_owner_token BLOB NOT NULL CHECK(length(retention_owner_token)=32),created_at TEXT NOT NULL,expires_at TEXT)""",
    ];
    private static readonly string[] AdditionalDefinitions =
    [
        "CREATE INDEX IX_local_ai_session_reports ON local_ai_runs(scope_kind,session_id,state,completed_at DESC,run_id DESC)",
        "CREATE TRIGGER local_ai_snapshots_update_rejected BEFORE UPDATE ON local_ai_snapshots WHEN NOT (local_ai_retention_delete_authorized('snapshot',OLD.snapshot_id)=1 AND OLD.scope_kind='session' AND OLD.payload_json IS NOT NULL AND OLD.evidence_index_json IS NOT NULL AND NEW.payload_json IS NULL AND NEW.evidence_index_json IS NULL AND NEW.snapshot_id=OLD.snapshot_id AND NEW.scope_kind=OLD.scope_kind AND NEW.session_id=OLD.session_id AND NEW.node_id IS OLD.node_id AND NEW.repository_id IS OLD.repository_id AND NEW.comparison_id IS OLD.comparison_id AND NEW.anchor_id=OLD.anchor_id AND NEW.payload_sha256=OLD.payload_sha256 AND NEW.evidence_index_sha256=OLD.evidence_index_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at AND NEW.expires_at IS OLD.expires_at) BEGIN SELECT RAISE(ABORT,'local_ai_snapshot_immutable'); END",
        "CREATE TRIGGER local_ai_results_update_rejected BEFORE UPDATE ON local_ai_results WHEN NOT (local_ai_retention_delete_authorized('result',OLD.result_id)=1 AND OLD.result_json IS NOT NULL AND NEW.result_json IS NULL AND NEW.result_id=OLD.result_id AND NEW.run_id=OLD.run_id AND NEW.result_sha256=OLD.result_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at AND NEW.expires_at IS OLD.expires_at) BEGIN SELECT RAISE(ABORT,'local_ai_result_immutable'); END",
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
            if (version == 1 && ownedCount == VersionOneDefinitions.Length + VersionOneAdditionalDefinitions.Length
                && HasExactSchema(connection, transaction, VersionOneDefinitions, VersionOneAdditionalDefinitions))
            {
                MigrateVersionOne(connection, transaction);
            }
            else if (version != Version || ownedCount != Definitions.Length + AdditionalDefinitions.Length || !HasExactSchema(connection, transaction)) Reject();
            transaction.Commit();
            return;
        }
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        foreach (var definition in Definitions) Execute(connection, transaction, definition + ";");
        foreach (var definition in AdditionalDefinitions) Execute(connection, transaction, definition + ";");
        if (!HasExactSchema(connection, transaction)) Reject();
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('local_ai_analysis',2);");
        transaction.Commit();
    }

    internal static bool IsValid(SqliteConnection connection, SqliteTransaction transaction) =>
        ReadVersion(connection, transaction) == Version && OwnedCount(connection, transaction) == Definitions.Length + AdditionalDefinitions.Length && HasExactSchema(connection, transaction);

    internal static bool ValidateExisting(SqliteConnection connection, SqliteTransaction transaction)
    {
        var version=ReadVersion(connection,transaction); var owned=OwnedCount(connection,transaction);
        if(version is null && owned==0) return false;
        if(version!=Version || owned!=Definitions.Length+AdditionalDefinitions.Length || !HasExactSchema(connection,transaction)) Reject();
        return true;
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
    private static bool HasExactSchema(SqliteConnection connection, SqliteTransaction transaction) =>
        HasExactSchema(connection, transaction, Definitions, AdditionalDefinitions);

    private static bool HasExactSchema(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyCollection<string> definitions, IReadOnlyCollection<string> additionalDefinitions)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name LIKE 'local_ai_%' OR name LIKE 'IX_local_ai_%' ORDER BY name;";
        using var reader = command.ExecuteReader(); var actual = new List<string>(); while (reader.Read()) actual.Add(Normalize(reader.GetString(0)));
        return actual.Order(StringComparer.Ordinal).SequenceEqual(definitions.Concat(additionalDefinitions).Select(Normalize).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }
    private static void MigrateVersionOne(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ValidateVersionOneRows(connection,transaction)) Reject();
        connection.CreateFunction<string,string>("local_ai_add_24h",static value=>DateTimeOffset.ParseExact(value,"O",CultureInfo.InvariantCulture,DateTimeStyles.None).AddHours(24).ToString("O",CultureInfo.InvariantCulture),isDeterministic:true);
        foreach (var name in new[] { "local_ai_snapshots_update_rejected", "local_ai_results_update_rejected", "local_ai_terminal_run_update_rejected" })
            Execute(connection, transaction, $"DROP TRIGGER {name};");
        Execute(connection, transaction, "DROP INDEX IX_local_ai_session_reports;");
        Execute(connection, transaction, "ALTER TABLE local_ai_snapshots RENAME TO local_ai_snapshots_v1;");
        Execute(connection, transaction, "ALTER TABLE local_ai_runs RENAME TO local_ai_runs_v1;");
        Execute(connection, transaction, "ALTER TABLE local_ai_results RENAME TO local_ai_results_v1;");
        foreach (var definition in Definitions) Execute(connection, transaction, definition + ";");
        Execute(connection, transaction, """
            INSERT INTO local_ai_snapshots(snapshot_id,scope_kind,session_id,node_id,repository_id,comparison_id,anchor_id,payload_json,payload_sha256,evidence_index_json,evidence_index_sha256,retention_owner_token,created_at,expires_at)
            SELECT snapshot_id,scope_kind,session_id,node_id,NULL,NULL,anchor_id,payload_json,payload_sha256,evidence_index_json,evidence_index_sha256,retention_owner_token,created_at,
              CASE scope_kind WHEN 'node' THEN local_ai_add_24h(created_at) END
            FROM local_ai_snapshots_v1;
            INSERT INTO local_ai_runs(run_id,snapshot_id,scope_kind,session_id,node_id,repository_id,comparison_id,state,provider,model,configuration_sha256,prompt_template_version,requested_at,started_at,completed_at,timeout_seconds,error_code,result_id,created_at,updated_at,expires_at)
            SELECT run_id,snapshot_id,scope_kind,session_id,node_id,NULL,NULL,state,provider,model,configuration_sha256,prompt_template_version,requested_at,started_at,completed_at,timeout_seconds,error_code,result_id,created_at,updated_at,
              CASE scope_kind WHEN 'node' THEN local_ai_add_24h(requested_at) END
            FROM local_ai_runs_v1;
            INSERT INTO local_ai_results(result_id,run_id,result_json,result_sha256,retention_owner_token,created_at,expires_at)
            SELECT x.result_id,x.run_id,x.result_json,x.result_sha256,x.retention_owner_token,x.created_at,r.expires_at
            FROM local_ai_results_v1 x JOIN local_ai_runs r ON r.run_id=x.run_id;
            """);
        if(!ValidateMigratedRows(connection,transaction))Reject();
        Execute(connection,transaction,"DROP TABLE local_ai_results_v1; DROP TABLE local_ai_runs_v1; DROP TABLE local_ai_snapshots_v1;");
        foreach (var definition in AdditionalDefinitions) Execute(connection, transaction, definition + ";");
        Execute(connection, transaction, "UPDATE schema_version SET version=2 WHERE component='local_ai_analysis' AND version=1;");
        if (!HasExactSchema(connection, transaction)) Reject();
    }
    private static bool ValidateVersionOneRows(SqliteConnection connection,SqliteTransaction transaction)
    {
        using(var invalid=connection.CreateCommand()){invalid.Transaction=transaction;invalid.CommandText="""
            SELECT EXISTS(
              SELECT 1 FROM local_ai_runs r LEFT JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id
              WHERE s.snapshot_id IS NULL OR r.scope_kind<>s.scope_kind OR r.session_id<>s.session_id OR r.node_id IS NOT s.node_id
                OR (r.state='queued' AND (r.started_at IS NOT NULL OR r.completed_at IS NOT NULL OR r.error_code IS NOT NULL OR r.result_id IS NOT NULL))
                OR (r.state='running' AND (r.started_at IS NULL OR r.completed_at IS NOT NULL OR r.error_code IS NOT NULL OR r.result_id IS NOT NULL))
                OR (r.state IN('succeeded','zero_findings') AND (r.started_at IS NULL OR r.completed_at IS NULL OR r.error_code IS NOT NULL OR r.result_id IS NULL))
                OR (r.state NOT IN('queued','running','succeeded','zero_findings') AND (r.started_at IS NULL OR r.completed_at IS NULL OR r.error_code<>r.state OR r.result_id IS NOT NULL))
              UNION ALL SELECT 1 FROM local_ai_results x LEFT JOIN local_ai_runs r ON r.run_id=x.run_id
              WHERE r.run_id IS NULL OR r.result_id<>x.result_id OR r.state NOT IN('succeeded','zero_findings') OR x.created_at<>r.completed_at
              UNION ALL SELECT 1 FROM local_ai_runs r WHERE r.result_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM local_ai_results x WHERE x.result_id=r.result_id AND x.run_id=r.run_id)
            );
            """;if(Convert.ToInt64(invalid.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)return false;}
        using(var snapshots=connection.CreateCommand()){snapshots.Transaction=transaction;snapshots.CommandText="SELECT snapshot_id,scope_kind,session_id,node_id,anchor_id,payload_json,payload_sha256,evidence_index_json,evidence_index_sha256,created_at FROM local_ai_snapshots;";using var reader=snapshots.ExecuteReader();while(reader.Read()){var node=reader.IsDBNull(3)?null:reader.GetString(3);if(!CanonicalTimestamp(reader.GetString(9))||!LocalAiAnalysisStoreV1.ValidateStoredSnapshotMetadata(reader.GetString(0),reader.GetString(1),reader.GetString(2),node,reader.GetString(4),reader.GetString(6),reader.GetString(8)))return false;if(!reader.IsDBNull(5)&&!LocalAiAnalysisStoreV1.ValidateStoredSnapshot(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),node,reader.GetString(4),(byte[])reader[5],reader.GetString(6),(byte[])reader[7],reader.GetString(8))))return false;}}
        using(var runs=connection.CreateCommand()){runs.Transaction=transaction;runs.CommandText="SELECT run_id,requested_at,started_at,completed_at,created_at,updated_at FROM local_ai_runs;";using var reader=runs.ExecuteReader();while(reader.Read()){if(!LocalAiResultValidatorV1.CanonicalUuid7(reader.GetString(0)))return false;for(var i=1;i<6;i++)if(!reader.IsDBNull(i)&&!CanonicalTimestamp(reader.GetString(i)))return false;}}
        using(var results=connection.CreateCommand()){results.Transaction=transaction;results.CommandText="SELECT result_id,result_json,result_sha256 FROM local_ai_results;";using var reader=results.ExecuteReader();while(reader.Read()){if(!LocalAiResultValidatorV1.CanonicalUuid7(reader.GetString(0))||!reader.IsDBNull(1)&&Convert.ToHexStringLower(SHA256.HashData((byte[])reader[1]))!=reader.GetString(2))return false;}}
        return true;
    }
    private static bool ValidateMigratedRows(SqliteConnection connection,SqliteTransaction transaction)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="""
            SELECT
              (SELECT COUNT(*) FROM local_ai_snapshots)=(SELECT COUNT(*) FROM local_ai_snapshots_v1)
              AND (SELECT COUNT(*) FROM local_ai_runs)=(SELECT COUNT(*) FROM local_ai_runs_v1)
              AND (SELECT COUNT(*) FROM local_ai_results)=(SELECT COUNT(*) FROM local_ai_results_v1)
              AND NOT EXISTS(SELECT 1 FROM local_ai_runs r LEFT JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id WHERE s.snapshot_id IS NULL OR r.scope_kind<>s.scope_kind OR r.session_id IS NOT s.session_id OR r.node_id IS NOT s.node_id)
              AND NOT EXISTS(SELECT 1 FROM local_ai_results x LEFT JOIN local_ai_runs r ON r.run_id=x.run_id WHERE r.run_id IS NULL OR r.result_id<>x.result_id OR x.expires_at IS NOT r.expires_at)
              AND NOT EXISTS(SELECT 1 FROM local_ai_snapshots n JOIN local_ai_snapshots_v1 o USING(snapshot_id) WHERE n.scope_kind<>o.scope_kind OR n.session_id<>o.session_id OR n.node_id IS NOT o.node_id OR n.anchor_id<>o.anchor_id OR n.payload_json IS NOT o.payload_json OR n.payload_sha256<>o.payload_sha256 OR n.evidence_index_json IS NOT o.evidence_index_json OR n.evidence_index_sha256<>o.evidence_index_sha256 OR n.retention_owner_token<>o.retention_owner_token OR n.created_at<>o.created_at)
              AND NOT EXISTS(SELECT 1 FROM local_ai_runs n JOIN local_ai_runs_v1 o USING(run_id) WHERE n.snapshot_id<>o.snapshot_id OR n.scope_kind<>o.scope_kind OR n.session_id<>o.session_id OR n.node_id IS NOT o.node_id OR n.state<>o.state OR n.provider<>o.provider OR n.model<>o.model OR n.configuration_sha256<>o.configuration_sha256 OR n.prompt_template_version<>o.prompt_template_version OR n.requested_at<>o.requested_at OR n.started_at IS NOT o.started_at OR n.completed_at IS NOT o.completed_at OR n.timeout_seconds<>o.timeout_seconds OR n.error_code IS NOT o.error_code OR n.result_id IS NOT o.result_id OR n.created_at<>o.created_at OR n.updated_at<>o.updated_at)
              AND NOT EXISTS(SELECT 1 FROM local_ai_results n JOIN local_ai_results_v1 o USING(result_id) WHERE n.run_id<>o.run_id OR n.result_json IS NOT o.result_json OR n.result_sha256<>o.result_sha256 OR n.retention_owner_token<>o.retention_owner_token OR n.created_at<>o.created_at);
            """;return Convert.ToInt64(command.ExecuteScalar(),CultureInfo.InvariantCulture)==1;
    }
    private static bool CanonicalTimestamp(string value)=>DateTimeOffset.TryParseExact(value,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)&&parsed.ToString("O",CultureInfo.InvariantCulture)==value;
    private static string Normalize(string sql)
    {
        var normalized=new StringBuilder(sql.Length); var previousWord=false;
        for(var index=0;index<sql.Length;)
        {
            if(char.IsWhiteSpace(sql[index])) { index++; continue; }
            var token=new StringBuilder(); var word=false;
            if(sql[index]=='\'')
            {
                token.Append(sql[index++]);
                while(index<sql.Length)
                {
                    token.Append(sql[index]);
                    if(sql[index++]!='\'') continue;
                    if(index<sql.Length && sql[index]=='\'') { token.Append(sql[index++]); continue; }
                    break;
                }
            }
            else if(char.IsLetterOrDigit(sql[index]) || sql[index]=='_')
            {
                word=true; while(index<sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index]=='_')) token.Append(sql[index++]);
            }
            else token.Append(sql[index++]);
            if(previousWord && word) normalized.Append(' ');
            normalized.Append(token); previousWord=word;
        }
        return normalized.ToString().TrimEnd(';');
    }
    private static bool Exists(SqliteConnection c, SqliteTransaction t, string name) { using var q = c.CreateCommand(); q.Transaction=t; q.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);"; q.Parameters.AddWithValue("$name",name); return Convert.ToInt64(q.ExecuteScalar()) != 0; }
    private static void Execute(SqliteConnection c, SqliteTransaction t, string sql) { using var q=c.CreateCommand(); q.Transaction=t; q.CommandText=sql; q.ExecuteNonQuery(); }
    private static void Reject() => throw new InvalidOperationException("Unsupported incomplete local_ai_analysis schema.");
}

internal static class LocalAiResultValidatorV1
{
    private static readonly string[] Root = ["scope", "snapshot", "summary", "findings", "improvement_suggestions", "limitations", "provenance"];
    private static readonly string[] Finding = ["finding_id", "title", "explanation", "evidence_state", "evidence_refs", "limitation"];
    private static readonly string[] Suggestion = ["suggestion_id", "target_kind", "target_label", "concrete_change", "rationale", "expected_effect", "risks_or_limitations", "evidence_refs"];
    private static readonly string[] SessionNodeScope = ["kind", "session_id", "node_id", "anchor_id"];
    private static readonly string[] RepositoryScope = ["kind", "repository_id", "anchor_id"];
    private static readonly string[] ComparisonScope = ["kind", "repository_id", "comparison_id", "anchor_id"];
    private static readonly string[] Snapshot = ["snapshot_id", "payload_sha256"];
    private static readonly string[] Provenance = ["provider", "model", "configuration_sha256", "prompt_template_version", "requested_at", "started_at", "completed_at", "snapshot_id", "snapshot_sha256", "coverage"];
    private static readonly string[] Coverage = ["included", "excluded", "content_available"];
    private static readonly HashSet<string> TargetKinds = ["instructions", "skill", "agent", "subagent_input", "tool_configuration"];

    internal static LocalAiResultValidationV1 Validate(ReadOnlySpan<byte> utf8, IReadOnlyCollection<string>? evidenceIndex)
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

    private static LocalAiResultValidationCodeV1 ValidateRefs(JsonElement value, IReadOnlyCollection<string>? evidence)
    {
        if (value.ValueKind != JsonValueKind.Array) return LocalAiResultValidationCodeV1.InvalidResult;
        var refs = value.EnumerateArray().ToArray();
        if (refs.Length is < 1 or > 16 || refs.Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))) return LocalAiResultValidationCodeV1.InvalidEvidence;
        return evidence is null || refs.All(item => evidence.Contains(item.GetString()!, StringComparer.Ordinal)) ? LocalAiResultValidationCodeV1.Valid : LocalAiResultValidationCodeV1.InvalidEvidence;
    }
    private static bool Exact(JsonElement element, string[] expected) => element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(expected.Order());
    private static bool Strings(JsonElement element, params string[] names) => names.All(name => element.GetProperty(name).ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetProperty(name).GetString()));
    private static LocalAiResultValidationV1 Invalid() => new(LocalAiResultValidationCodeV1.InvalidResult);
    private static bool ValidScope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("kind", out var kindProperty)
            || kindProperty.ValueKind != JsonValueKind.String || !value.TryGetProperty("anchor_id", out var anchorProperty)
            || anchorProperty.ValueKind != JsonValueKind.String) return false;
        var kind = kindProperty.GetString();
        try
        {
            LocalAiScopeIdentityV1 identity;
            if (kind is "session" or "node")
            {
                if (!Exact(value, SessionNodeScope) || value.GetProperty("session_id").ValueKind != JsonValueKind.String) return false;
                var node = value.GetProperty("node_id");
                if (node.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return false;
                identity = LocalAiScopeIdentityV1.Create(kind, value.GetProperty("session_id").GetString(),
                    node.ValueKind == JsonValueKind.Null ? null : node.GetString(), null, null);
            }
            else if (kind == "repository_selection")
            {
                if (!Exact(value, RepositoryScope) || value.GetProperty("repository_id").ValueKind != JsonValueKind.String) return false;
                identity = LocalAiScopeIdentityV1.Create(kind, null, null, value.GetProperty("repository_id").GetString(), null);
            }
            else if (kind == "comparison")
            {
                if (!Exact(value, ComparisonScope) || value.GetProperty("repository_id").ValueKind != JsonValueKind.String
                    || value.GetProperty("comparison_id").ValueKind != JsonValueKind.String) return false;
                identity = LocalAiScopeIdentityV1.Create(kind, null, null, value.GetProperty("repository_id").GetString(),
                    value.GetProperty("comparison_id").GetString());
            }
            else return false;
            return anchorProperty.GetString() == identity.AnchorId;
        }
        catch (ArgumentException) { return false; }
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
    private static bool ValidLimitations(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);
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
    internal const int MaximumSnapshotDocumentBytes = 1_048_576;
    private readonly string databasePath;
    private readonly RetentionCatalogStore? retentionCatalog;
    private readonly TimeProvider clock;
    private readonly Action? retainedResultMaterialized;
    internal LocalAiAnalysisStoreV1(string databasePath, RetentionCatalogStore? retentionCatalog = null, TimeProvider? timeProvider = null,Action? retainedResultMaterializedForTesting=null) { ArgumentException.ThrowIfNullOrWhiteSpace(databasePath); this.databasePath = databasePath; this.retentionCatalog = retentionCatalog; clock = timeProvider ?? TimeProvider.System; retainedResultMaterialized=retainedResultMaterializedForTesting; }

    internal void InsertSnapshot(LocalAiSnapshotV1 snapshot)
    {
        ValidateUuid7(snapshot.SnapshotId); var identity=LocalAiScopeIdentityV1.Create(snapshot.ScopeKind,snapshot.SessionId,snapshot.NodeId,snapshot.RepositoryId,snapshot.ComparisonId);
        if (snapshot.ScopeKind == "session" && retentionCatalog is null) throw new InvalidOperationException("local_ai_retention_required");
        if(snapshot.PayloadCanonicalJson.Length>MaximumSnapshotDocumentBytes||snapshot.EvidenceIndexCanonicalJson.Length>MaximumSnapshotDocumentBytes)throw new InvalidOperationException("local_ai_snapshot_scope_too_large");
        var payload = Canonical(snapshot.PayloadCanonicalJson); var evidence = Canonical(snapshot.EvidenceIndexCanonicalJson);
        if(!ValidateStoredSnapshot(new(snapshot.SnapshotId,snapshot.ScopeKind,snapshot.SessionId,snapshot.NodeId,snapshot.AnchorId,snapshot.PayloadCanonicalJson,Hash(snapshot.PayloadCanonicalJson),snapshot.EvidenceIndexCanonicalJson,Hash(snapshot.EvidenceIndexCanonicalJson),snapshot.RepositoryId,snapshot.ComparisonId)))throw new InvalidOperationException("local_ai_snapshot_not_canonical");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction=transaction;
        var ownerToken=RandomNumberGenerator.GetBytes(32); var created=clock.GetUtcNow();
        var expires=identity.Kind switch { LocalAiScopeKindV1.Session => (DateTimeOffset?)null, LocalAiScopeKindV1.Comparison => snapshot.ExpiresAt is { } bound ? Min(created.AddHours(24),bound) : throw new ArgumentException("local_ai_comparison_expiry_required"), _ => created.AddHours(24) };
        command.CommandText = """
            INSERT INTO local_ai_snapshots(snapshot_id,scope_kind,session_id,node_id,repository_id,comparison_id,anchor_id,payload_json,payload_sha256,evidence_index_json,evidence_index_sha256,retention_owner_token,created_at,expires_at)
            VALUES($id,$scope,$session,$node,$repository,$comparison,$anchor,$payload,$payloadHash,$evidence,$evidenceHash,$owner,$created,$expires) ON CONFLICT(snapshot_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", snapshot.SnapshotId); command.Parameters.AddWithValue("$scope", snapshot.ScopeKind); command.Parameters.AddWithValue("$session", (object?)snapshot.SessionId??DBNull.Value);
        command.Parameters.AddWithValue("$node", (object?)snapshot.NodeId ?? DBNull.Value); command.Parameters.AddWithValue("$anchor", snapshot.AnchorId);
        command.Parameters.AddWithValue("$repository",(object?)snapshot.RepositoryId??DBNull.Value);command.Parameters.AddWithValue("$comparison",(object?)snapshot.ComparisonId??DBNull.Value);command.Parameters.AddWithValue("$expires",expires is null?DBNull.Value:expires.Value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture));
        command.Parameters.Add("$payload", SqliteType.Blob).Value=payload; command.Parameters.AddWithValue("$payloadHash", Hash(payload)); command.Parameters.Add("$evidence",SqliteType.Blob).Value=evidence;
        command.Parameters.AddWithValue("$evidenceHash",Hash(evidence)); command.Parameters.Add("$owner",SqliteType.Blob).Value=ownerToken; command.Parameters.AddWithValue("$created",created.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() == 1) { if(snapshot.ScopeKind=="session") retentionCatalog?.RegisterLocalAiRaw(connection,transaction,"snapshot",snapshot.SnapshotId,created,ownerToken); transaction.Commit(); return; }
        using var read=connection.CreateCommand(); read.Transaction=transaction; read.CommandText="SELECT scope_kind,session_id,node_id,repository_id,comparison_id,anchor_id,payload_json,evidence_index_json,created_at,expires_at FROM local_ai_snapshots WHERE snapshot_id=$id;"; read.Parameters.AddWithValue("$id",snapshot.SnapshotId);
        using var reader=read.ExecuteReader(); if(!reader.Read())throw new InvalidOperationException("local_ai_snapshot_conflict");var storedCreated=DateTimeOffset.Parse(reader.GetString(8),CultureInfo.InvariantCulture);var expectedStoredExpiry=identity.Kind switch{LocalAiScopeKindV1.Session=>(DateTimeOffset?)null,LocalAiScopeKindV1.Comparison=>snapshot.ExpiresAt is { } bound?Min(storedCreated.AddHours(24),bound):throw new ArgumentException("local_ai_comparison_expiry_required"),_=>storedCreated.AddHours(24)};if(reader.GetString(0)!=snapshot.ScopeKind || (reader.IsDBNull(1)?null:reader.GetString(1))!=snapshot.SessionId || (reader.IsDBNull(2)?null:reader.GetString(2))!=snapshot.NodeId || (reader.IsDBNull(3)?null:reader.GetString(3))!=snapshot.RepositoryId || (reader.IsDBNull(4)?null:reader.GetString(4))!=snapshot.ComparisonId || reader.GetString(5)!=snapshot.AnchorId || reader.IsDBNull(6) || reader.IsDBNull(7) || !((byte[])reader[6]).SequenceEqual(payload) || !((byte[])reader[7]).SequenceEqual(evidence) || (reader.IsDBNull(9)?null:reader.GetString(9))!=(expectedStoredExpiry is null?null:expectedStoredExpiry.Value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture))) throw new InvalidOperationException("local_ai_snapshot_conflict"); reader.Close(); transaction.Commit();
    }

    internal LocalAiRunV1 CreateRun(LocalAiRunRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request); var snapshotId=request.SnapshotId; var scopeKind=request.ScopeKind; var sessionId=request.SessionId; var nodeId=request.NodeId;
        ValidateUuid7(snapshotId); var identity=LocalAiScopeIdentityV1.Create(scopeKind,sessionId,nodeId,request.RepositoryId,request.ComparisonId); ValidateMetadata(request); if(scopeKind=="session" && retentionCatalog is null) throw new InvalidOperationException("local_ai_retention_required"); var timeout=request.TimeoutSeconds ?? 60; if (timeout is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(request.TimeoutSeconds));
        var runId=Guid.CreateVersion7().ToString(); var now=clock.GetUtcNow().ToUniversalTime().ToString("O",CultureInfo.InvariantCulture); using var connection=Open();
        DateTimeOffset? requestExpiry;
        using (var snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = "SELECT expires_at FROM local_ai_snapshots WHERE snapshot_id=$id AND scope_kind=$scope AND session_id IS $session AND node_id IS $node AND repository_id IS $repository AND comparison_id IS $comparison;";
            snapshot.Parameters.AddWithValue("$id", snapshotId); snapshot.Parameters.AddWithValue("$scope", scopeKind); snapshot.Parameters.AddWithValue("$session",(object?)sessionId??DBNull.Value); snapshot.Parameters.AddWithValue("$node", (object?)nodeId ?? DBNull.Value);snapshot.Parameters.AddWithValue("$repository",(object?)request.RepositoryId??DBNull.Value);snapshot.Parameters.AddWithValue("$comparison",(object?)request.ComparisonId??DBNull.Value);
            var snapshotExpiry=snapshot.ExecuteScalar(); if(snapshotExpiry is null)throw new InvalidOperationException("local_ai_snapshot_scope_mismatch");
            requestExpiry=identity.Kind==LocalAiScopeKindV1.Session?null:identity.Kind==LocalAiScopeKindV1.Comparison?Min(request.RequestedAt.AddHours(24),DateTimeOffset.Parse((string)snapshotExpiry,CultureInfo.InvariantCulture)):request.RequestedAt.AddHours(24);
        }
        using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO local_ai_runs(run_id,snapshot_id,scope_kind,session_id,node_id,repository_id,comparison_id,state,provider,model,configuration_sha256,prompt_template_version,requested_at,started_at,completed_at,timeout_seconds,error_code,result_id,created_at,updated_at,expires_at) VALUES($run,$snapshot,$scope,$session,$node,$repository,$comparison,'queued',$provider,$model,$configuration,$template,$requested,NULL,NULL,$timeout,NULL,NULL,$now,$now,$expires);";
        command.Parameters.AddWithValue("$run",runId); command.Parameters.AddWithValue("$snapshot",snapshotId); command.Parameters.AddWithValue("$scope",scopeKind); command.Parameters.AddWithValue("$session",(object?)sessionId??DBNull.Value); command.Parameters.AddWithValue("$node",(object?)nodeId??DBNull.Value);command.Parameters.AddWithValue("$repository",(object?)request.RepositoryId??DBNull.Value);command.Parameters.AddWithValue("$comparison",(object?)request.ComparisonId??DBNull.Value);command.Parameters.AddWithValue("$expires",requestExpiry is null?DBNull.Value:requestExpiry.Value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$provider",request.Provider); command.Parameters.AddWithValue("$model",request.Model); command.Parameters.AddWithValue("$configuration",request.ConfigurationSha256); command.Parameters.AddWithValue("$template",request.PromptTemplateVersion); command.Parameters.AddWithValue("$requested",request.RequestedAt.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$timeout",timeout); command.Parameters.AddWithValue("$now",now); command.ExecuteNonQuery(); return new(runId,timeout);
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
        using var evidenceCommand=connection.CreateCommand(); evidenceCommand.CommandText="SELECT s.evidence_index_json,s.payload_sha256,s.snapshot_id,s.scope_kind,s.session_id,s.node_id,s.anchor_id,r.provider,r.model,r.configuration_sha256,r.prompt_template_version,r.requested_at,r.started_at,s.repository_id,s.comparison_id,r.expires_at FROM local_ai_runs r JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id WHERE r.run_id=$id;"; evidenceCommand.Parameters.AddWithValue("$id",runId);
        using var expectedReader=evidenceCommand.ExecuteReader(); if(!expectedReader.Read()) throw new InvalidOperationException("local_ai_run_missing");
        var completion=(completedAt??DateTimeOffset.UtcNow).ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
        var expected=new LocalAiStoredResultInvariantV1((byte[])expectedReader[0],expectedReader.GetString(1),expectedReader.GetString(2),expectedReader.GetString(3),expectedReader.IsDBNull(4)?null:expectedReader.GetString(4),expectedReader.IsDBNull(5)?null:expectedReader.GetString(5),expectedReader.GetString(6),expectedReader.GetString(7),expectedReader.GetString(8),expectedReader.GetString(9),expectedReader.GetString(10),expectedReader.GetString(11),expectedReader.GetString(12),completion,"",expectedReader.IsDBNull(13)?null:expectedReader.GetString(13),expectedReader.IsDBNull(14)?null:expectedReader.GetString(14));
        var runExpires=expectedReader.IsDBNull(15)?(DateTimeOffset?)null:DateTimeOffset.Parse(expectedReader.GetString(15),CultureInfo.InvariantCulture);
        if(expected.ScopeKind=="session" && retentionCatalog is null) throw new InvalidOperationException("local_ai_retention_required");
        var evidenceIndex=expected.EvidenceIndex??throw new InvalidOperationException("local_ai_evidence_index_missing");
        expectedReader.Close(); var refs=ReadEvidence(evidenceIndex); var validation=LocalAiResultValidatorV1.Validate(result,refs);
        if(validation.Code==LocalAiResultValidationCodeV1.Valid && !MatchesExpected(validation.CanonicalBytes!,expected)) validation=new(LocalAiResultValidationCodeV1.InvalidResult);
        if(validation.Code!=LocalAiResultValidationCodeV1.Valid) { var failed=validation.Code==LocalAiResultValidationCodeV1.InvalidEvidence?LocalAiRunStateV1.InvalidEvidence:LocalAiRunStateV1.InvalidResult; TransitionRun(runId,failed,failed==LocalAiRunStateV1.InvalidEvidence?"invalid_evidence":"invalid_result"); return failed; }
        var canonical=validation.CanonicalBytes!; var root=JsonDocument.Parse(canonical).RootElement; var state=root.GetProperty("findings").GetArrayLength()==0?LocalAiRunStateV1.ZeroFindings:LocalAiRunStateV1.Succeeded;if(!ValidateStoredResult(canonical,expected with{RunState=Wire(state)}))throw new InvalidOperationException("local_ai_result_invariant_invalid");
        using var transaction=connection.BeginTransaction(); using var insert=connection.CreateCommand(); insert.Transaction=transaction; var resultId=Guid.CreateVersion7().ToString(); var now=completion; var ownerToken=RandomNumberGenerator.GetBytes(32);
        insert.CommandText="INSERT INTO local_ai_results(result_id,run_id,result_json,result_sha256,retention_owner_token,created_at,expires_at) VALUES($result,$run,$json,$hash,$owner,$now,$expires);"; insert.Parameters.AddWithValue("$result",resultId); insert.Parameters.AddWithValue("$run",runId); insert.Parameters.Add("$json",SqliteType.Blob).Value=canonical; insert.Parameters.AddWithValue("$hash",Hash(canonical)); insert.Parameters.Add("$owner",SqliteType.Blob).Value=ownerToken; insert.Parameters.AddWithValue("$now",now);insert.Parameters.AddWithValue("$expires",runExpires is null?DBNull.Value:Min(DateTimeOffset.Parse(completion,CultureInfo.InvariantCulture).AddHours(24),runExpires.Value).ToString("O",CultureInfo.InvariantCulture)); insert.ExecuteNonQuery();
        using var update=connection.CreateCommand(); update.Transaction=transaction; update.CommandText="UPDATE local_ai_runs SET state=$state,completed_at=$now,result_id=$result,error_code=NULL,updated_at=$now WHERE run_id=$run AND state='running' AND completed_at IS NULL AND result_id IS NULL;"; update.Parameters.AddWithValue("$state",Wire(state)); update.Parameters.AddWithValue("$now",now); update.Parameters.AddWithValue("$result",resultId); update.Parameters.AddWithValue("$run",runId); if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("local_ai_run_transition_conflict"); if(expected.ScopeKind=="session") retentionCatalog?.RegisterLocalAiRaw(connection,transaction,"result",resultId,DateTimeOffset.Parse(completion,CultureInfo.InvariantCulture),ownerToken); transaction.Commit(); return state;
    }

    internal int DeleteExpiredTransientRuns(DateTimeOffset now)
    {
        var cutoff=now.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture); using var connection=Open(); using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="CREATE TEMP TABLE local_ai_cleanup_candidates(run_id TEXT PRIMARY KEY,snapshot_id TEXT NOT NULL); CREATE TEMP TABLE local_ai_cleanup_snapshots(snapshot_id TEXT PRIMARY KEY); INSERT INTO local_ai_cleanup_candidates SELECT run_id,snapshot_id FROM local_ai_runs WHERE scope_kind<>'session' AND expires_at<=$cutoff; INSERT INTO local_ai_cleanup_snapshots SELECT snapshot_id FROM local_ai_cleanup_candidates; INSERT OR IGNORE INTO local_ai_cleanup_snapshots SELECT snapshot_id FROM local_ai_snapshots WHERE scope_kind<>'session' AND expires_at<=$cutoff AND NOT EXISTS(SELECT 1 FROM local_ai_runs WHERE local_ai_runs.snapshot_id=local_ai_snapshots.snapshot_id); DELETE FROM local_ai_results WHERE run_id IN (SELECT run_id FROM local_ai_cleanup_candidates); DELETE FROM local_ai_runs WHERE run_id IN (SELECT run_id FROM local_ai_cleanup_candidates); DELETE FROM local_ai_snapshots WHERE snapshot_id IN (SELECT snapshot_id FROM local_ai_cleanup_snapshots) AND NOT EXISTS(SELECT 1 FROM local_ai_runs WHERE local_ai_runs.snapshot_id=local_ai_snapshots.snapshot_id);"; command.Parameters.AddWithValue("$cutoff",cutoff); command.ExecuteNonQuery();
        using var count=connection.CreateCommand(); count.Transaction=transaction; count.CommandText="SELECT COUNT(*) FROM local_ai_cleanup_candidates;"; var deleted=Convert.ToInt32(count.ExecuteScalar(),CultureInfo.InvariantCulture); transaction.Commit(); return deleted;
    }

    internal static int DeleteExpiredTransientRunsIfPresent(string path,DateTimeOffset now)
    {
        var store=new LocalAiAnalysisStoreV1(path); using var connection=store.Open(); using(var validation=connection.BeginTransaction()){if(!LocalAiAnalysisSchemaV1.ValidateExisting(connection,validation)){validation.Commit();return 0;}validation.Commit();} return store.DeleteExpiredTransientRuns(now);
    }

    internal LocalAiReportPageV1 GetSessionReports(string sessionId, int? limit, string? cursor)
    {
        var take=Math.Min(limit??20,100); if(take<1) throw new ArgumentOutOfRangeException(nameof(limit)); var cursorValue=DecodeCursor(cursor);
        using var connection=Open(); using var command=connection.CreateCommand(); command.CommandText="""
            SELECT r.run_id,x.result_id,r.state,COALESCE(x.created_at,r.completed_at),x.result_sha256
            FROM local_ai_runs r LEFT JOIN local_ai_results x ON x.run_id=r.run_id
            WHERE r.scope_kind='session' AND r.session_id=$session AND r.state IN ('succeeded','zero_findings')
              AND ($cursor IS NULL OR (COALESCE(x.created_at,r.completed_at) || '|' || r.run_id) < $cursor)
            ORDER BY COALESCE(x.created_at,r.completed_at) DESC,r.run_id DESC LIMIT $limit;
            """; command.Parameters.AddWithValue("$session",sessionId); command.Parameters.AddWithValue("$cursor",(object?)cursorValue??DBNull.Value); command.Parameters.AddWithValue("$limit",take+1);
        using var reader=command.ExecuteReader(); var metadata=new List<(string RunId,string? ResultId,LocalAiRunStateV1 State,DateTimeOffset CreatedAt,string? Hash)>(); while(reader.Read()) metadata.Add((reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1),Parse(reader.GetString(2)),DateTimeOffset.Parse(reader.GetString(3),CultureInfo.InvariantCulture),reader.IsDBNull(4)?null:reader.GetString(4))); reader.Close();
        var rows=metadata.Select(item=>{var content=item.ResultId is null?null:ReadRetainedResult(item.ResultId);return new LocalAiReportV1(item.RunId,item.ResultId,item.State,item.CreatedAt,content,item.Hash,content is null?"expired":"retained");}).ToList();
        var hasMore=rows.Count>take; if(hasMore) rows.RemoveAt(rows.Count-1); var next=hasMore?EncodeCursor(rows[^1].CreatedAt.ToString("O",CultureInfo.InvariantCulture)+"|"+rows[^1].RunId):null; return new(rows,next);
    }

    internal byte[]? ReadRetainedResult(string resultId)
    {
        ValidateUuid7(resultId); if(retentionCatalog is null) throw new InvalidOperationException("local_ai_retention_required");
        var request=new RetentionReadRequest(new(retentionCatalog.StoreInstanceId,RetentionStoreKind.AnalysisRunRaw,"local_ai:result:"+resultId),RetentionReadKind.Access,clock.GetUtcNow(),null);
        var result=retentionCatalog.ReadAsync(request,(connection,transaction,grant,_)=>
        {
            using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="""
                SELECT x.result_json FROM local_ai_results x
                JOIN retention_items i ON i.item_id=$retention_read_item_id AND i.store_instance_id=$retention_store_instance_id
                  AND i.store_kind='analysis_run_raw' AND i.source_item_id='local_ai:result:'||x.result_id AND i.revision=$retention_read_revision
                JOIN retention_leases l ON l.item_id=i.item_id AND l.lease_kind=$retention_read_lease_kind AND l.owner=$retention_read_lease_owner
                  AND l.generation=$retention_read_lease_generation AND l.expires_at=$retention_read_lease_expires_at
                WHERE x.result_id=$id AND x.retention_owner_token=$retention_read_source_token AND x.result_json IS NOT NULL;
                """; command.Parameters.AddWithValue("$id",resultId); command.Parameters.AddWithValue("$retention_store_instance_id",retentionCatalog.StoreInstanceId); grant.BindAdmissionSelectorCapability(command); var bytes=command.ExecuteScalar() as byte[];if(bytes is not null)retainedResultMaterialized?.Invoke();return ValueTask.FromResult(bytes);
        },CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if(result.Lease is null) return null; var lease=result.Lease;
        try { byte[] bytes; using(var reference=lease.AcquireValueReference()) bytes=reference.Value.ToArray(); if(lease.TrySealRawResponse()!=RetentionRawTerminalResult.Sealed) return null; return bytes; }
        finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
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
    private static void ValidateUuid7(string value){if(!LocalAiResultValidatorV1.CanonicalUuid7(value)) throw new ArgumentException("local_ai_uuid7_required");}
    private static bool Allowed(LocalAiRunStateV1 current,LocalAiRunStateV1 next)=>current==LocalAiRunStateV1.Queued&&next==LocalAiRunStateV1.Running || current==LocalAiRunStateV1.Running&&next is LocalAiRunStateV1.ProviderFailed or LocalAiRunStateV1.ProviderPartial or LocalAiRunStateV1.InvalidResult or LocalAiRunStateV1.InvalidEvidence or LocalAiRunStateV1.StaleSnapshot or LocalAiRunStateV1.ScopeTooLarge or LocalAiRunStateV1.TimedOut or LocalAiRunStateV1.Canceled;
    private static LocalAiRunStateV1 ReadState(SqliteConnection c,string id){using var q=c.CreateCommand();q.CommandText="SELECT state FROM local_ai_runs WHERE run_id=$id;";q.Parameters.AddWithValue("$id",id);return Parse(q.ExecuteScalar() as string??throw new InvalidOperationException("local_ai_run_missing"));}
    private static string Wire(LocalAiRunStateV1 value)=>value switch{LocalAiRunStateV1.Queued=>"queued",LocalAiRunStateV1.Running=>"running",LocalAiRunStateV1.Succeeded=>"succeeded",LocalAiRunStateV1.ZeroFindings=>"zero_findings",LocalAiRunStateV1.ProviderFailed=>"provider_failed",LocalAiRunStateV1.ProviderPartial=>"provider_partial",LocalAiRunStateV1.InvalidResult=>"invalid_result",LocalAiRunStateV1.InvalidEvidence=>"invalid_evidence",LocalAiRunStateV1.StaleSnapshot=>"stale_snapshot",LocalAiRunStateV1.ScopeTooLarge=>"scope_too_large",LocalAiRunStateV1.TimedOut=>"timed_out",LocalAiRunStateV1.Canceled=>"canceled",_=>throw new ArgumentOutOfRangeException(nameof(value))};
    private static LocalAiRunStateV1 Parse(string value)=>value switch{"queued"=>LocalAiRunStateV1.Queued,"running"=>LocalAiRunStateV1.Running,"succeeded"=>LocalAiRunStateV1.Succeeded,"zero_findings"=>LocalAiRunStateV1.ZeroFindings,"provider_failed"=>LocalAiRunStateV1.ProviderFailed,"provider_partial"=>LocalAiRunStateV1.ProviderPartial,"invalid_result"=>LocalAiRunStateV1.InvalidResult,"invalid_evidence"=>LocalAiRunStateV1.InvalidEvidence,"stale_snapshot"=>LocalAiRunStateV1.StaleSnapshot,"scope_too_large"=>LocalAiRunStateV1.ScopeTooLarge,"timed_out"=>LocalAiRunStateV1.TimedOut,"canceled"=>LocalAiRunStateV1.Canceled,_=>throw new InvalidOperationException("local_ai_state_invalid")};
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static DateTimeOffset Min(DateTimeOffset left,DateTimeOffset right)=>left<=right?left:right;
    private static string EncodeCursor(string value)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string? DecodeCursor(string? value)
    { try{return value is null?null:Encoding.UTF8.GetString(Convert.FromBase64String(value));}catch(FormatException){throw new ArgumentException("local_ai_cursor_invalid");} }
    private static bool MatchesExpected(byte[] bytes,LocalAiStoredResultInvariantV1 expected)
    {
        using var document=JsonDocument.Parse(bytes); var root=document.RootElement; var scope=root.GetProperty("scope"); var snapshot=root.GetProperty("snapshot"); var provenance=root.GetProperty("provenance");
        var identity=LocalAiScopeIdentityV1.Create(expected.ScopeKind,expected.SessionId,expected.NodeId,expected.RepositoryId,expected.ComparisonId);
        var scopeMatches=identity.Kind switch
        {
            LocalAiScopeKindV1.Session or LocalAiScopeKindV1.Node => scope.GetProperty("session_id").GetString()==expected.SessionId && (scope.GetProperty("node_id").ValueKind==JsonValueKind.Null?null:scope.GetProperty("node_id").GetString())==expected.NodeId,
            LocalAiScopeKindV1.RepositorySelection => scope.GetProperty("repository_id").GetString()==expected.RepositoryId,
            LocalAiScopeKindV1.Comparison => scope.GetProperty("repository_id").GetString()==expected.RepositoryId&&scope.GetProperty("comparison_id").GetString()==expected.ComparisonId,
            _ => false,
        };
        return scope.GetProperty("kind").GetString()==expected.ScopeKind && scopeMatches && scope.GetProperty("anchor_id").GetString()==expected.AnchorId
            && snapshot.GetProperty("snapshot_id").GetString()==expected.SnapshotId && snapshot.GetProperty("payload_sha256").GetString()==expected.PayloadSha256
            && provenance.GetProperty("provider").GetString()==expected.Provider && provenance.GetProperty("model").GetString()==expected.Model
            && provenance.GetProperty("configuration_sha256").GetString()==expected.ConfigurationSha256 && provenance.GetProperty("prompt_template_version").GetString()==expected.Template
            && provenance.GetProperty("requested_at").GetString()==expected.RequestedAt && provenance.GetProperty("snapshot_id").GetString()==expected.SnapshotId
            && provenance.GetProperty("snapshot_sha256").GetString()==expected.PayloadSha256 && provenance.GetProperty("started_at").GetString()==expected.StartedAt
            && provenance.GetProperty("completed_at").GetString()==expected.CompletedAt;
    }
    internal static bool ValidateStoredResult(byte[] bytes,LocalAiStoredResultInvariantV1 expected)
    {
        return expected.EvidenceIndex is not null&&ValidateStoredResultCore(bytes,expected,ReadEvidence(expected.EvidenceIndex));
    }
    internal static bool ValidateStoredResultWithoutEvidenceMembership(byte[] bytes,LocalAiStoredResultInvariantV1 expected)=>ValidateStoredResultCore(bytes,expected,null);
    private static bool ValidateStoredResultCore(byte[] bytes,LocalAiStoredResultInvariantV1 expected,IReadOnlyCollection<string>? evidence)
    {var validation=LocalAiResultValidatorV1.Validate(bytes,evidence);if(validation.Code!=LocalAiResultValidationCodeV1.Valid||!validation.CanonicalBytes!.SequenceEqual(bytes)||!MatchesExpected(bytes,expected))return false;using var document=JsonDocument.Parse(bytes);var findings=document.RootElement.GetProperty("findings").GetArrayLength();return expected.RunState=="zero_findings"?findings==0:expected.RunState=="succeeded"&&findings>0;}
    internal static bool ValidateStoredSnapshot(LocalAiStoredSnapshotInvariantV1 value)
    {try{if(value.Payload.Length>MaximumSnapshotDocumentBytes||value.EvidenceIndex.Length>MaximumSnapshotDocumentBytes||!ValidateStoredSnapshotMetadata(value.SnapshotId,value.ScopeKind,value.SessionId,value.NodeId,value.AnchorId,value.PayloadSha256,value.EvidenceIndexSha256,value.RepositoryId,value.ComparisonId)||Hash(value.Payload)!=value.PayloadSha256||Hash(value.EvidenceIndex)!=value.EvidenceIndexSha256||!Canonical(value.Payload).SequenceEqual(value.Payload)||!Canonical(value.EvidenceIndex).SequenceEqual(value.EvidenceIndex))return false;ValidateEvidenceIndex(value.EvidenceIndex);return true;}catch(Exception exception)when(exception is ArgumentException or InvalidOperationException or JsonException){return false;}}
    internal static bool ValidateStoredSnapshotMetadata(string snapshotId,string scopeKind,string? sessionId,string? nodeId,string anchorId,string payloadSha256,string evidenceIndexSha256,string? repositoryId=null,string? comparisonId=null)
    {try{ValidateUuid7(snapshotId);var identity=LocalAiScopeIdentityV1.Create(scopeKind,sessionId,nodeId,repositoryId,comparisonId);return anchorId==identity.AnchorId&&HashValue(payloadSha256)&&HashValue(evidenceIndexSha256);}catch(ArgumentException){return false;}}
    private static bool HashValue(string value)=>value.Length==64&&value.All(character=>character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

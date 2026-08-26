using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionSchemaV1
{
    internal const string ComponentName = "local_workspace_projection";
    internal const int Version = 4;
    internal static readonly string[] TableNames =
    [
        "local_workspace_sessions", "local_workspace_session_sources",
        "local_workspace_session_models", "local_workspace_session_search_facts", "local_workspace_session_activity",
        "local_workspace_token_observations", "local_workspace_span_facts", "local_workspace_projection_state",
        "local_workspace_execution_headers", "local_workspace_nodes", "local_workspace_node_edges", "local_workspace_node_content_refs",
    ];

    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> V3Definitions =
    [
        new("table", "local_workspace_sessions", "local_workspace_sessions", """
            CREATE TABLE local_workspace_sessions (
                session_id TEXT PRIMARY KEY,
                sort_group INTEGER NOT NULL CHECK(sort_group IN (0,1)),
                sort_epoch_ms INTEGER NOT NULL,
                label_state TEXT NOT NULL,
                label_text TEXT NULL,
                label_source_identity TEXT NULL,
                label_expires_at TEXT NULL,
                status TEXT NOT NULL CHECK(status IN ('active','completed','failed','unknown')),
                completeness TEXT NOT NULL CHECK(completeness IN ('unbound','partial','rich','full')),
                source_state TEXT NOT NULL CHECK(source_state IN ('recorded','not_observed','projection_invalid')),
                model_state TEXT NOT NULL CHECK(model_state IN ('recorded','not_observed','projection_invalid')),
                timing_state TEXT NOT NULL,
                started_at TEXT NULL,
                ended_at TEXT NULL,
                last_seen_at TEXT NULL,
                last_seen_epoch_ms INTEGER NULL,
                duration_ms INTEGER NULL CHECK(duration_ms IS NULL OR duration_ms >= 0),
                capture_notes TEXT NOT NULL,
                revision_seed TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((label_state='recorded' AND label_text IS NOT NULL AND label_source_identity IS NOT NULL AND label_expires_at IS NOT NULL) OR (label_state<>'recorded' AND label_text IS NULL AND label_source_identity IS NULL AND label_expires_at IS NULL)),
                CHECK((sort_group=1 AND sort_epoch_ms=0) OR sort_group=0),
                CHECK((last_seen_at IS NULL)=(last_seen_epoch_ms IS NULL))
            );
            """),
        new("table", "local_workspace_session_search_facts", "local_workspace_session_search_facts", """
            CREATE TABLE local_workspace_session_search_facts (
                session_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('label','skill','tool')),
                source_identity TEXT NOT NULL,
                normalized_text TEXT NOT NULL CHECK(length(normalized_text)>0),
                expires_at TEXT NULL,
                PRIMARY KEY(session_id,kind,source_identity,normalized_text),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("index", "local_workspace_search_facts_by_text", "local_workspace_session_search_facts", "CREATE INDEX local_workspace_search_facts_by_text ON local_workspace_session_search_facts(normalized_text,session_id);"),
        new("index", "local_workspace_search_facts_by_session", "local_workspace_session_search_facts", "CREATE INDEX local_workspace_search_facts_by_session ON local_workspace_session_search_facts(session_id,kind);"),
        new("table", "local_workspace_session_sources", "local_workspace_session_sources", """
            CREATE TABLE local_workspace_session_sources (
                session_id TEXT NOT NULL,
                source TEXT NOT NULL CHECK(source IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code')),
                PRIMARY KEY(session_id,source),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_session_models", "local_workspace_session_models", """
            CREATE TABLE local_workspace_session_models (
                session_id TEXT NOT NULL,
                model TEXT NOT NULL,
                PRIMARY KEY(session_id,model),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_session_activity", "local_workspace_session_activity", """
            CREATE TABLE local_workspace_session_activity (
                session_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('skill','tool','subagent','error','retry')),
                state TEXT NOT NULL,
                count INTEGER NULL CHECK(count IS NULL OR count >= 0),
                PRIMARY KEY(session_id,kind),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((state='recorded')=(count IS NOT NULL))
            );
            """),
        new("table", "local_workspace_token_observations", "local_workspace_token_observations", """
            CREATE TABLE local_workspace_token_observations (
                session_id TEXT NOT NULL,
                execution_id TEXT NOT NULL,
                authority TEXT NOT NULL CHECK(authority IN ('session_run','llm_span')),
                authority_rank INTEGER NOT NULL CHECK((authority='session_run' AND authority_rank=0) OR (authority='llm_span' AND authority_rank=1)),
                source_identity TEXT NOT NULL,
                input_tokens INTEGER NULL CHECK(input_tokens IS NULL OR input_tokens >= 0),
                output_tokens INTEGER NULL CHECK(output_tokens IS NULL OR output_tokens >= 0),
                total_tokens INTEGER NULL CHECK(total_tokens IS NULL OR total_tokens >= 0),
                reasoning_tokens INTEGER NULL CHECK(reasoning_tokens IS NULL OR reasoning_tokens >= 0),
                cache_read_tokens INTEGER NULL CHECK(cache_read_tokens IS NULL OR cache_read_tokens >= 0),
                cache_creation_tokens INTEGER NULL CHECK(cache_creation_tokens IS NULL OR cache_creation_tokens >= 0),
                PRIMARY KEY(session_id,execution_id,authority,source_identity),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_projection_state", "local_workspace_projection_state", """
            CREATE TABLE local_workspace_projection_state (
                projector_key TEXT PRIMARY KEY CHECK(projector_key='local-workspace-projection-v1'),
                session_frontier TEXT NULL,
                refreshed_at TEXT NOT NULL
            );
            """),
        new("table", "local_workspace_span_facts", "local_workspace_span_facts", """
            CREATE TABLE local_workspace_span_facts (
                raw_record_id INTEGER NOT NULL,
                span_ordinal INTEGER NOT NULL,
                retry_count INTEGER NULL CHECK(retry_count IS NULL OR retry_count >= 0),
                producer_total_tokens INTEGER NULL CHECK(producer_total_tokens IS NULL OR producer_total_tokens >= 0),
                PRIMARY KEY(raw_record_id,span_ordinal)
            );
            """),
    ];
    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> Definitions =
    [
        .. V3Definitions,
        new("table", "local_workspace_execution_headers", "local_workspace_execution_headers", """
            CREATE TABLE local_workspace_execution_headers (
                execution_id TEXT PRIMARY KEY CHECK(length(execution_id)=36),
                session_id TEXT NOT NULL,
                source_kind TEXT NOT NULL CHECK(source_kind='session_run'),
                source_identity TEXT NOT NULL,
                source_ordinal INTEGER NOT NULL CHECK(source_ordinal>=0),
                lifecycle TEXT NOT NULL CHECK(lifecycle IN ('selected','started','completed','failed','deselected','unknown')),
                status TEXT NOT NULL CHECK(status IN ('active','completed','failed','unknown')),
                model TEXT NULL,
                time_authority TEXT NOT NULL CHECK(time_authority IN ('recorded','missing','invalid')),
                start_utc_ticks INTEGER NULL,
                end_utc_ticks INTEGER NULL,
                duration_ms INTEGER NULL CHECK(duration_ms IS NULL OR duration_ms>=0),
                activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(activity_state IN ('recorded','not_observed','capture_gap','source_unsupported','certification_pending','unavailable')),
                activity_count INTEGER NULL CHECK(activity_count IS NULL OR activity_count>=0),
                token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(token_state IN ('recorded','not_observed','inconsistent')),
                input_tokens INTEGER NULL CHECK(input_tokens IS NULL OR input_tokens>=0),
                output_tokens INTEGER NULL CHECK(output_tokens IS NULL OR output_tokens>=0),
                total_tokens INTEGER NULL CHECK(total_tokens IS NULL OR total_tokens>=0),
                reasoning_tokens INTEGER NULL CHECK(reasoning_tokens IS NULL OR reasoning_tokens>=0),
                cache_read_tokens INTEGER NULL CHECK(cache_read_tokens IS NULL OR cache_read_tokens>=0),
                cache_creation_tokens INTEGER NULL CHECK(cache_creation_tokens IS NULL OR cache_creation_tokens>=0),
                UNIQUE(session_id,source_kind,source_identity),
                UNIQUE(session_id,source_ordinal),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((time_authority='recorded')=(start_utc_ticks IS NOT NULL)),
                CHECK((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (time_authority='recorded' AND end_utc_ticks>=start_utc_ticks AND duration_ms=(end_utc_ticks-start_utc_ticks)/10000)),
                CHECK((activity_state='recorded')=(activity_count IS NOT NULL)),
                CHECK((token_state='not_observed')=(input_tokens IS NULL AND output_tokens IS NULL AND total_tokens IS NULL AND reasoning_tokens IS NULL AND cache_read_tokens IS NULL AND cache_creation_tokens IS NULL))
            );
            """),
        new("index", "local_workspace_executions_by_session", "local_workspace_execution_headers", "CREATE INDEX local_workspace_executions_by_session ON local_workspace_execution_headers(session_id,time_authority,start_utc_ticks,source_ordinal,execution_id);"),
        new("table", "local_workspace_nodes", "local_workspace_nodes", """
            CREATE TABLE local_workspace_nodes (
                node_id TEXT PRIMARY KEY CHECK(length(node_id)=37 AND substr(node_id,1,5)='node-'),
                session_id TEXT NOT NULL,
                execution_id TEXT NOT NULL,
                source_kind TEXT NOT NULL CHECK(source_kind IN ('execution_root','session_event','skill_invocation','unknown_relation_group')),
                source_identity TEXT NOT NULL,
                source_ordinal INTEGER NOT NULL CHECK(source_ordinal>=0),
                parent_node_id TEXT NULL,
                relationship_authority TEXT NOT NULL CHECK(relationship_authority IN ('exact','explicit','unknown')),
                kind TEXT NOT NULL CHECK(kind IN ('execution','agent','skill','tool','subagent','event','error','retry','permission','unknown_relation_group')),
                name_state TEXT NOT NULL CHECK(name_state IN ('recorded','not_observed','invalid')),
                name_text TEXT NULL,
                lifecycle TEXT NOT NULL CHECK(lifecycle IN ('selected','started','completed','failed','deselected','unknown')),
                status TEXT NOT NULL CHECK(status IN ('active','completed','failed','unknown')),
                time_authority TEXT NOT NULL CHECK(time_authority IN ('recorded','missing','invalid')),
                start_utc_ticks INTEGER NULL,
                activity_state TEXT NOT NULL CHECK(activity_state IN ('recorded','not_observed','capture_gap','source_unsupported','certification_pending','unavailable')),
                activity_count INTEGER NULL CHECK(activity_count IS NULL OR activity_count>=0),
                token_state TEXT NOT NULL CHECK(token_state IN ('recorded','not_observed','inconsistent')),
                input_tokens INTEGER NULL CHECK(input_tokens IS NULL OR input_tokens>=0),
                output_tokens INTEGER NULL CHECK(output_tokens IS NULL OR output_tokens>=0),
                total_tokens INTEGER NULL CHECK(total_tokens IS NULL OR total_tokens>=0),
                reasoning_tokens INTEGER NULL CHECK(reasoning_tokens IS NULL OR reasoning_tokens>=0),
                cache_read_tokens INTEGER NULL CHECK(cache_read_tokens IS NULL OR cache_read_tokens>=0),
                cache_creation_tokens INTEGER NULL CHECK(cache_creation_tokens IS NULL OR cache_creation_tokens>=0),
                retry_relation_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(retry_relation_state='not_observed'),
                recovery_relation_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(recovery_relation_state='not_observed'),
                trace_id TEXT NULL,
                span_id TEXT NULL,
                event_id TEXT NULL,
                otel_source_identity TEXT NULL,
                sdk_source_identity TEXT NULL,
                UNIQUE(session_id,source_kind,source_identity),
                UNIQUE(execution_id,source_ordinal,node_id),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                FOREIGN KEY(execution_id) REFERENCES local_workspace_execution_headers(execution_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                FOREIGN KEY(parent_node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((name_state='recorded')=(name_text IS NOT NULL)),
                CHECK((time_authority='recorded')=(start_utc_ticks IS NOT NULL)),
                CHECK((trace_id IS NULL)=(span_id IS NULL)),
                CHECK(source_kind<>'skill_invocation' OR otel_source_identity IS NOT NULL OR sdk_source_identity IS NOT NULL),
                CHECK((activity_state='recorded')=(activity_count IS NOT NULL)),
                CHECK((token_state='not_observed')=(input_tokens IS NULL AND output_tokens IS NULL AND total_tokens IS NULL AND reasoning_tokens IS NULL AND cache_read_tokens IS NULL AND cache_creation_tokens IS NULL))
            );
            """),
        new("index", "local_workspace_nodes_by_parent", "local_workspace_nodes", "CREATE INDEX local_workspace_nodes_by_parent ON local_workspace_nodes(execution_id,parent_node_id,time_authority,start_utc_ticks,source_ordinal,node_id);"),
        new("table", "local_workspace_node_edges", "local_workspace_node_edges", """
            CREATE TABLE local_workspace_node_edges (
                node_id TEXT NOT NULL,
                related_node_id TEXT NOT NULL,
                relation_kind TEXT NOT NULL CHECK(relation_kind IN ('parent','retry','recovery')),
                relationship_authority TEXT NOT NULL CHECK(relationship_authority IN ('exact','explicit')),
                source_ordinal INTEGER NOT NULL CHECK(source_ordinal>=0),
                PRIMARY KEY(node_id,related_node_id,relation_kind),
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                FOREIGN KEY(related_node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_node_content_refs", "local_workspace_node_content_refs", """
            CREATE TABLE local_workspace_node_content_refs (
                node_id TEXT NOT NULL,
                part TEXT NOT NULL CHECK(part IN ('instruction','tool_input','tool_result','error_message','subagent_input','event_content')),
                store_kind TEXT NOT NULL,
                source_item_id TEXT NOT NULL,
                locator_kind TEXT NOT NULL CHECK(locator_kind IN ('whole_event','json_pointer')),
                json_pointer TEXT NULL CHECK(json_pointer IS NULL OR json_pointer IN ('/prompt','/tool_input','/tool_response','/error','/agent_id')),
                selected_utf8_bytes INTEGER NULL CHECK(selected_utf8_bytes IS NULL OR selected_utf8_bytes>=0),
                revision_input TEXT NOT NULL,
                retention_owner_token BLOB NULL CHECK(retention_owner_token IS NULL OR (typeof(retention_owner_token)='blob' AND length(retention_owner_token)=32)),
                availability_state TEXT NOT NULL CHECK(availability_state IN ('available','not_captured','expired','deleted','read_denied','oversized','invalid')),
                PRIMARY KEY(node_id,part),
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((availability_state='available')=(retention_owner_token IS NOT NULL)),
                CHECK((locator_kind='whole_event' AND json_pointer IS NULL AND part='event_content') OR (locator_kind='json_pointer' AND json_pointer IS NOT NULL AND part<>'event_content'))
            );
            """),
    ];

    internal static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ExpectedObjects { get; } =
        SqliteOwnedSchemaAuthority.Compile(Definitions);
    private static readonly SqliteOwnedSchemaDefinition V2Sessions = new("table", "local_workspace_sessions", "local_workspace_sessions", """
        CREATE TABLE local_workspace_sessions (
            session_id TEXT PRIMARY KEY,
            sort_group INTEGER NOT NULL CHECK(sort_group IN (0,1)),
            sort_epoch_ms INTEGER NOT NULL CHECK(sort_epoch_ms >= 0),
            label_state TEXT NOT NULL,
            label_text TEXT NULL,
            label_search_text TEXT NULL,
            label_source_identity TEXT NULL,
            label_expires_at TEXT NULL,
            status TEXT NOT NULL CHECK(status IN ('active','completed','failed','unknown')),
            completeness TEXT NOT NULL CHECK(completeness IN ('unbound','partial','rich','full')),
            source_state TEXT NOT NULL CHECK(source_state IN ('recorded','not_observed','projection_invalid')),
            model_state TEXT NOT NULL CHECK(model_state IN ('recorded','not_observed','projection_invalid')),
            timing_state TEXT NOT NULL,
            started_at TEXT NULL,
            ended_at TEXT NULL,
            last_seen_at TEXT NOT NULL,
            duration_ms INTEGER NULL CHECK(duration_ms IS NULL OR duration_ms >= 0),
            capture_notes TEXT NOT NULL,
            revision_seed TEXT NOT NULL,
            FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
            CHECK((label_state='recorded' AND label_text IS NOT NULL AND label_search_text IS NOT NULL AND label_source_identity IS NOT NULL AND label_expires_at IS NOT NULL) OR (label_state<>'recorded' AND label_text IS NULL AND label_search_text IS NULL AND label_source_identity IS NULL AND label_expires_at IS NULL)),
            CHECK((started_at IS NULL AND sort_group=1 AND sort_epoch_ms=0) OR (started_at IS NOT NULL AND sort_group=0))
        );
        """);
    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> V2Definitions =
        [V2Sessions, .. V3Definitions.Where(static definition => definition.Name is not ("local_workspace_sessions" or "local_workspace_session_search_facts" or "local_workspace_search_facts_by_text" or "local_workspace_search_facts_by_session"))];
    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> V2ExpectedObjects = SqliteOwnedSchemaAuthority.Compile(V2Definitions);
    private static readonly string[] V2TableNames = V2Definitions.Where(static definition => definition.Type == "table").Select(static definition => definition.Name).ToArray();
    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> V1Definitions =
        V2Definitions.Where(static definition => definition.Name != "local_workspace_span_facts").ToArray();
    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> V1ExpectedObjects = SqliteOwnedSchemaAuthority.Compile(V1Definitions);
    internal static IEnumerable<SqliteOwnedSchemaObject> OwnedObjects => ExpectedObjects.Values;
    internal static IEnumerable<string> ExactV1SchemaSql => V1Definitions.Select(static definition => definition.Sql);
    internal static IEnumerable<string> ExactV2SchemaSql => V2Definitions.Select(static definition => definition.Sql);
    internal static IEnumerable<string> ExactV3SchemaSql => V3Definitions.Select(static definition => definition.Sql);

    internal static void Ensure(SqliteConnection connection, DateTimeOffset now)
    {
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction, now);
        transaction.Commit();
    }

    internal static void Ensure(
        SqliteConnection connection,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority skillRegistryAuthority)
    {
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction, now, skillRegistryAuthority);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
        => Ensure(connection, transaction, now, null);

    internal static void Ensure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!HasSessionV14(connection, transaction))
            throw new InvalidOperationException("local_workspace_projection_component_dependency_invalid");
        var version = ReadVersion(connection, transaction);
        var owned = ReadOwnedObjects(connection, transaction);
        if (version == 1 && SqliteOwnedSchemaAuthority.Equal(owned, V1ExpectedObjects))
        {
            Execute(connection, transaction, V2Definitions.Single(static definition => definition.Name == "local_workspace_span_facts").Sql);
            Execute(connection, transaction, "UPDATE schema_version SET version=2 WHERE component='local_workspace_projection' AND version=1;");
            BackfillSpanFacts(connection, transaction);
            version = 2;
            owned = ReadOwnedObjects(connection, transaction);
        }
        if (version == 2 && SqliteOwnedSchemaAuthority.Equal(owned, V2ExpectedObjects))
        {
            foreach (var table in V2TableNames.Reverse()) Execute(connection, transaction, $"DROP TABLE {table};");
            foreach (var definition in V3Definitions) Execute(connection, transaction, definition.Sql);
            BackfillSpanFacts(connection, transaction);
            RefreshProjection(connection, transaction, now, skillRegistryAuthority);
            Execute(connection, transaction, "UPDATE schema_version SET version=3 WHERE component='local_workspace_projection' AND version=2;");
            version = 3;
            owned = ReadOwnedObjects(connection, transaction);
        }
        if (version == 3 && SqliteOwnedSchemaAuthority.Equal(owned, SqliteOwnedSchemaAuthority.Compile(V3Definitions)))
        {
            foreach (var definition in Definitions.Skip(V3Definitions.Count)) Execute(connection, transaction, definition.Sql);
            RefreshProjection(connection, transaction, now, skillRegistryAuthority);
            ValidateSemanticRows(connection, transaction);
            Execute(connection, transaction, "UPDATE schema_version SET version=4 WHERE component='local_workspace_projection' AND version=3;");
            Validate(connection, transaction);
            return;
        }
        if (version is not null || owned.Count != 0)
        {
            Validate(connection, transaction);
            RefreshProjection(connection, transaction, now, skillRegistryAuthority);
            ValidateSemanticRows(connection, transaction);
            return;
        }
        foreach (var definition in Definitions) Execute(connection, transaction, definition.Sql);
        BackfillSpanFacts(connection, transaction);
        RefreshProjection(connection, transaction, now, skillRegistryAuthority);
        ValidateSemanticRows(connection, transaction);
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);");
        Validate(connection, transaction);
    }

    private static void RefreshProjection(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        if (skillRegistryAuthority is null)
            LocalWorkspaceProjectionStore.RefreshStructural(connection, transaction, now);
        else
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, now, skillRegistryAuthority);
    }

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (ReadVersion(connection, transaction) != Version
            || !SqliteOwnedSchemaAuthority.Equal(ReadOwnedObjects(connection, transaction), ExpectedObjects))
            throw new InvalidOperationException("Unsupported incomplete local_workspace_projection schema version 4.");
    }

    internal static void ValidateCurrentOrExactLegacy(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = ReadVersion(connection, transaction);
        var owned = ReadOwnedObjects(connection, transaction);
        if (version == Version && SqliteOwnedSchemaAuthority.Equal(owned, ExpectedObjects)) return;
        if (version == 3 && SqliteOwnedSchemaAuthority.Equal(owned, SqliteOwnedSchemaAuthority.Compile(V3Definitions))) return;
        if (version == 2 && SqliteOwnedSchemaAuthority.Equal(owned, V2ExpectedObjects)) return;
        if (version == 1 && SqliteOwnedSchemaAuthority.Equal(owned, V1ExpectedObjects)) return;
        throw new InvalidOperationException("Unsupported incomplete local_workspace_projection schema.");
    }

    private static void ValidateSemanticRows(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
              EXISTS(SELECT 1 FROM local_workspace_execution_headers h
                     WHERE h.execution_id<>local_workspace_execution_id(h.source_kind,h.source_identity))
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n
                     WHERE n.node_id<>local_workspace_node_id(n.source_kind,n.source_identity)
                        OR NOT EXISTS(SELECT 1 FROM local_workspace_execution_headers h WHERE h.execution_id=n.execution_id AND h.session_id=n.session_id)
                        OR (n.parent_node_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM local_workspace_nodes p WHERE p.node_id=n.parent_node_id AND p.execution_id=n.execution_id)))
              OR EXISTS(SELECT 1 FROM local_workspace_node_edges e
                     WHERE NOT EXISTS(SELECT 1 FROM local_workspace_nodes n JOIN local_workspace_nodes r ON r.node_id=e.related_node_id AND r.execution_id=n.execution_id WHERE n.node_id=e.node_id))
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id
                     WHERE c.store_kind<>'session_event_content' OR n.source_kind<>'session_event' OR n.source_identity<>c.source_item_id)
              OR EXISTS(SELECT 1 FROM local_workspace_nodes WHERE source_kind='skill_invocation' AND otel_source_identity IS NULL AND sdk_source_identity IS NULL)
              OR EXISTS(SELECT 1 FROM local_workspace_execution_headers GROUP BY session_id HAVING COUNT(*)>256)
              OR EXISTS(SELECT 1 FROM local_workspace_nodes GROUP BY session_id HAVING COUNT(*)>4096);
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("local_workspace_projection_semantic_validation_failed");
    }


    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ReadOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(connection, transaction, static (name, table) =>
            name.StartsWith("local_workspace_", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("local_workspace_", StringComparison.OrdinalIgnoreCase));

    private static long? ReadVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version,typeof(version) FROM schema_version WHERE component='local_workspace_projection';";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(1) != "integer") return long.MinValue;
        var value = reader.GetInt64(0);
        return reader.Read() ? long.MinValue : value;
    }

    private static bool HasSessionV14(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT (SELECT version FROM schema_version WHERE component='session'),
                   EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='sessions'),
                   EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='session_runs'),
                   EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='session_events');
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) && reader.GetInt64(0) == 14
            && reader.GetInt64(1) == 1 && reader.GetInt64(2) == 1 && reader.GetInt64(3) == 1;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void BackfillSpanFacts(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var installed = connection.CreateCommand();
        installed.Transaction = transaction;
        installed.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='raw_records') AND EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='monitor_spans');";
        if (Convert.ToInt64(installed.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return;

        var facts = new List<object>();
        using (var records = connection.CreateCommand())
        {
            records.Transaction = transaction;
            records.CommandText = "SELECT id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version FROM raw_records ORDER BY id;";
            using var reader = records.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var receivedAt))
                    throw new InvalidOperationException("local_workspace_projection_raw_timestamp_invalid");
                var record = new RawTelemetryRecord(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), receivedAt,
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt32(6));
                foreach (var span in MonitorSpanProjectionBuilder.Build(record))
                    facts.Add(new { raw = record.Id, ordinal = span.SpanOrdinal, retry = span.RetryCount, total = span.ProducerTotalTokens });
            }
        }
        if (facts.Count == 0) return;
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens)
            SELECT CAST(value->>'raw' AS INTEGER),CAST(value->>'ordinal' AS INTEGER),
              CASE WHEN json_type(value,'$.retry')='null' THEN NULL ELSE CAST(value->>'retry' AS INTEGER) END,
              CASE WHEN json_type(value,'$.total')='null' THEN NULL ELSE CAST(value->>'total' AS INTEGER) END
            FROM json_each($facts)
            WHERE EXISTS(SELECT 1 FROM monitor_spans s WHERE s.raw_record_id=CAST(value->>'raw' AS INTEGER) AND s.span_ordinal=CAST(value->>'ordinal' AS INTEGER));
            """;
        insert.Parameters.AddWithValue("$facts", System.Text.Json.JsonSerializer.Serialize(facts));
        insert.ExecuteNonQuery();
    }
}

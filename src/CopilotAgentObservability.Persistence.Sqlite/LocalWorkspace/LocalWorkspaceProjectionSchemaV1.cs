using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.RawReplay;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionSchemaV1
{
    internal const string ComponentName = "local_workspace_projection";
    internal const int Version = 5;

    internal static LocalWorkspaceProjectionInstallationState ReadInstallationState(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var version = ReadVersion(connection, transaction);
        var owned = ReadOwnedObjects(connection, transaction);
        if (version is null && owned.Count == 0) return LocalWorkspaceProjectionInstallationState.Absent;
        if (version == Version && SqliteOwnedSchemaAuthority.Equal(owned, ExpectedObjects))
            return LocalWorkspaceProjectionInstallationState.Current;
        return LocalWorkspaceProjectionInstallationState.Unsupported;
    }
    internal static readonly string[] TableNames =
    [
        "local_workspace_sessions", "local_workspace_session_sources",
        "local_workspace_session_models", "local_workspace_session_search_facts", "local_workspace_session_activity",
        "local_workspace_token_observations", "local_workspace_span_facts", "local_workspace_projection_state",
        "local_workspace_execution_headers", "local_workspace_nodes", "local_workspace_node_edges", "local_workspace_content_tombstones", "local_workspace_node_content_refs",
        "local_workspace_semantic_receipts", "local_workspace_node_source_references", "local_workspace_tool_metadata", "local_workspace_skill_metadata", "local_workspace_subagent_lifecycle",
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
    private static readonly SqliteOwnedSchemaDefinition V4Sessions = new("table", "local_workspace_sessions", "local_workspace_sessions",
        V3Definitions[0].Sql.Replace(
            "revision_seed TEXT NOT NULL,",
            "revision_seed TEXT NOT NULL,\n                node_overflow INTEGER NOT NULL CHECK(node_overflow IN (0,1)),",
            StringComparison.Ordinal));
    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> V4Definitions =
    [
        V4Sessions,
        .. V3Definitions.Skip(1),
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
                trace_id TEXT NULL CHECK(trace_id IS NULL OR length(trace_id)=32),
                time_authority TEXT NOT NULL CHECK(time_authority IN ('recorded','missing','invalid')),
                start_utc_ticks INTEGER NULL,
                end_utc_ticks INTEGER NULL,
                duration_ms INTEGER NULL CHECK(duration_ms IS NULL OR duration_ms>=0),
                skill_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(skill_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported','certification_pending','projection_invalid')),
                skill_activity_count INTEGER NULL CHECK(skill_activity_count IS NULL OR skill_activity_count>=0),
                tool_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(tool_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                tool_activity_count INTEGER NULL CHECK(tool_activity_count IS NULL OR tool_activity_count>=0),
                subagent_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(subagent_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                subagent_activity_count INTEGER NULL CHECK(subagent_activity_count IS NULL OR subagent_activity_count>=0),
                error_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(error_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                error_activity_count INTEGER NULL CHECK(error_activity_count IS NULL OR error_activity_count>=0),
                retry_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(retry_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                retry_activity_count INTEGER NULL CHECK(retry_activity_count IS NULL OR retry_activity_count>=0),
                token_authority TEXT NOT NULL DEFAULT 'none' CHECK(token_authority IN ('session_run','llm_span','mixed','none')),
                token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(token_state IN ('recorded','not_observed','inconsistent')),
                available_execution_count INTEGER NOT NULL DEFAULT 0 CHECK(available_execution_count IN (0,1)),
                total_execution_count INTEGER NOT NULL DEFAULT 1 CHECK(total_execution_count=1),
                input_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(input_token_state IN ('recorded','not_observed','inconsistent')),
                input_tokens INTEGER NULL CHECK(input_tokens IS NULL OR input_tokens>=0),
                output_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(output_token_state IN ('recorded','not_observed','inconsistent')),
                output_tokens INTEGER NULL CHECK(output_tokens IS NULL OR output_tokens>=0),
                total_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(total_token_state IN ('recorded','not_observed','inconsistent')),
                total_tokens INTEGER NULL CHECK(total_tokens IS NULL OR total_tokens>=0),
                reasoning_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(reasoning_token_state IN ('recorded','not_observed','inconsistent')),
                reasoning_tokens INTEGER NULL CHECK(reasoning_tokens IS NULL OR reasoning_tokens>=0),
                cache_read_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(cache_read_token_state IN ('recorded','not_observed','inconsistent')),
                cache_read_tokens INTEGER NULL CHECK(cache_read_tokens IS NULL OR cache_read_tokens>=0),
                cache_creation_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(cache_creation_token_state IN ('recorded','not_observed','inconsistent')),
                cache_creation_tokens INTEGER NULL CHECK(cache_creation_tokens IS NULL OR cache_creation_tokens>=0),
                new_input_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(new_input_token_state IN ('recorded','not_observed','inconsistent')),
                new_input_tokens INTEGER NULL CHECK(new_input_tokens IS NULL OR new_input_tokens>=0),
                cache_read_ratio_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(cache_read_ratio_state IN ('recorded','not_observed','inconsistent')),
                cache_read_ratio_basis_points INTEGER NULL CHECK(cache_read_ratio_basis_points IS NULL OR cache_read_ratio_basis_points BETWEEN 0 AND 10000),
                UNIQUE(session_id,source_kind,source_identity),
                UNIQUE(session_id,source_ordinal),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((time_authority='recorded')=(start_utc_ticks IS NOT NULL)),
                CHECK((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (time_authority='recorded' AND end_utc_ticks>=start_utc_ticks AND duration_ms=(end_utc_ticks-start_utc_ticks)/10000)),
                CHECK((skill_activity_state='recorded')=(skill_activity_count IS NOT NULL)), CHECK((tool_activity_state='recorded')=(tool_activity_count IS NOT NULL)),
                CHECK((subagent_activity_state='recorded')=(subagent_activity_count IS NOT NULL)), CHECK((error_activity_state='recorded')=(error_activity_count IS NOT NULL)), CHECK((retry_activity_state='recorded')=(retry_activity_count IS NOT NULL)),
                CHECK((input_token_state='recorded')=(input_tokens IS NOT NULL)), CHECK((output_token_state='recorded')=(output_tokens IS NOT NULL)), CHECK((total_token_state='recorded')=(total_tokens IS NOT NULL)),
                CHECK((reasoning_token_state='recorded')=(reasoning_tokens IS NOT NULL)), CHECK((cache_read_token_state='recorded')=(cache_read_tokens IS NOT NULL)), CHECK((cache_creation_token_state='recorded')=(cache_creation_tokens IS NOT NULL)),
                CHECK((new_input_token_state='recorded')=(new_input_tokens IS NOT NULL)), CHECK((cache_read_ratio_state='recorded')=(cache_read_ratio_basis_points IS NOT NULL)),
                CHECK((token_authority='none')=(available_execution_count=0)), CHECK((token_state='recorded')=(available_execution_count=1))
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
                end_utc_ticks INTEGER NULL,
                duration_ms INTEGER NULL CHECK(duration_ms IS NULL OR duration_ms>=0),
                skill_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(skill_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported','certification_pending','projection_invalid')),
                skill_activity_count INTEGER NULL CHECK(skill_activity_count IS NULL OR skill_activity_count>=0),
                tool_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(tool_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                tool_activity_count INTEGER NULL CHECK(tool_activity_count IS NULL OR tool_activity_count>=0),
                subagent_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(subagent_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                subagent_activity_count INTEGER NULL CHECK(subagent_activity_count IS NULL OR subagent_activity_count>=0),
                error_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(error_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                error_activity_count INTEGER NULL CHECK(error_activity_count IS NULL OR error_activity_count>=0),
                retry_activity_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(retry_activity_state IN ('recorded','not_observed','capture_gap','source_unsupported')),
                retry_activity_count INTEGER NULL CHECK(retry_activity_count IS NULL OR retry_activity_count>=0),
                token_authority TEXT NOT NULL DEFAULT 'none' CHECK(token_authority IN ('session_run','llm_span','mixed','none')),
                token_state TEXT NOT NULL CHECK(token_state IN ('recorded','not_observed','inconsistent')),
                available_execution_count INTEGER NOT NULL DEFAULT 0 CHECK(available_execution_count IN (0,1)), total_execution_count INTEGER NOT NULL DEFAULT 1 CHECK(total_execution_count=1),
                input_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(input_token_state IN ('recorded','not_observed','inconsistent')),
                input_tokens INTEGER NULL CHECK(input_tokens IS NULL OR input_tokens>=0),
                output_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(output_token_state IN ('recorded','not_observed','inconsistent')),
                output_tokens INTEGER NULL CHECK(output_tokens IS NULL OR output_tokens>=0),
                total_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(total_token_state IN ('recorded','not_observed','inconsistent')),
                total_tokens INTEGER NULL CHECK(total_tokens IS NULL OR total_tokens>=0),
                reasoning_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(reasoning_token_state IN ('recorded','not_observed','inconsistent')),
                reasoning_tokens INTEGER NULL CHECK(reasoning_tokens IS NULL OR reasoning_tokens>=0),
                cache_read_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(cache_read_token_state IN ('recorded','not_observed','inconsistent')),
                cache_read_tokens INTEGER NULL CHECK(cache_read_tokens IS NULL OR cache_read_tokens>=0),
                cache_creation_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(cache_creation_token_state IN ('recorded','not_observed','inconsistent')),
                cache_creation_tokens INTEGER NULL CHECK(cache_creation_tokens IS NULL OR cache_creation_tokens>=0),
                new_input_token_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(new_input_token_state IN ('recorded','not_observed','inconsistent')), new_input_tokens INTEGER NULL CHECK(new_input_tokens IS NULL OR new_input_tokens>=0),
                cache_read_ratio_state TEXT NOT NULL DEFAULT 'not_observed' CHECK(cache_read_ratio_state IN ('recorded','not_observed','inconsistent')), cache_read_ratio_basis_points INTEGER NULL CHECK(cache_read_ratio_basis_points IS NULL OR cache_read_ratio_basis_points BETWEEN 0 AND 10000),
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
                CHECK((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (time_authority='recorded' AND end_utc_ticks>=start_utc_ticks AND duration_ms=(end_utc_ticks-start_utc_ticks)/10000)),
                CHECK((trace_id IS NULL)=(span_id IS NULL)),
                CHECK(source_kind<>'skill_invocation' OR otel_source_identity IS NOT NULL OR sdk_source_identity IS NOT NULL),
                CHECK((skill_activity_state='recorded')=(skill_activity_count IS NOT NULL)), CHECK((tool_activity_state='recorded')=(tool_activity_count IS NOT NULL)), CHECK((subagent_activity_state='recorded')=(subagent_activity_count IS NOT NULL)), CHECK((error_activity_state='recorded')=(error_activity_count IS NOT NULL)), CHECK((retry_activity_state='recorded')=(retry_activity_count IS NOT NULL)),
                CHECK((input_token_state='recorded')=(input_tokens IS NOT NULL)), CHECK((output_token_state='recorded')=(output_tokens IS NOT NULL)), CHECK((total_token_state='recorded')=(total_tokens IS NOT NULL)), CHECK((reasoning_token_state='recorded')=(reasoning_tokens IS NOT NULL)), CHECK((cache_read_token_state='recorded')=(cache_read_tokens IS NOT NULL)), CHECK((cache_creation_token_state='recorded')=(cache_creation_tokens IS NOT NULL)), CHECK((new_input_token_state='recorded')=(new_input_tokens IS NOT NULL)), CHECK((cache_read_ratio_state='recorded')=(cache_read_ratio_basis_points IS NOT NULL)),
                CHECK((token_authority='none')=(available_execution_count=0)), CHECK((token_state='recorded')=(available_execution_count=1))
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
        new("table", "local_workspace_content_tombstones", "local_workspace_content_tombstones", """
            CREATE TABLE local_workspace_content_tombstones (
                source_item_id TEXT PRIMARY KEY,
                part TEXT NOT NULL CHECK(part IN ('instruction','tool_input','tool_result','error_message','subagent_input','event_content')),
                locator_kind TEXT NOT NULL CHECK(locator_kind IN ('whole_event','json_pointer')),
                json_pointer TEXT NULL CHECK(json_pointer IS NULL OR json_pointer IN ('/prompt','/tool_input','/tool_response','/error','/agent_id')),
                selected_utf8_bytes INTEGER NULL CHECK(selected_utf8_bytes IS NULL OR selected_utf8_bytes>=0),
                deleted_at TEXT NOT NULL,
                retention_item_id TEXT NOT NULL,
                retention_revision INTEGER NOT NULL CHECK(retention_revision>0),
                CHECK((locator_kind='whole_event' AND json_pointer IS NULL AND part='event_content') OR (locator_kind='json_pointer' AND json_pointer IS NOT NULL AND part<>'event_content'))
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
                retention_item_id TEXT NULL,
                retention_store_instance_id TEXT NULL,
                source_captured_at TEXT NULL,
                source_expires_at TEXT NULL,
                retention_revision INTEGER NULL CHECK(retention_revision IS NULL OR retention_revision>0),
                retention_ownership_receipt BLOB NULL CHECK(retention_ownership_receipt IS NULL OR (typeof(retention_ownership_receipt)='blob' AND length(retention_ownership_receipt)=32)),
                retention_owner_token BLOB NULL CHECK(retention_owner_token IS NULL OR (typeof(retention_owner_token)='blob' AND length(retention_owner_token)=32)),
                availability_state TEXT NOT NULL CHECK(availability_state IN ('available','not_captured','expired','deleted','read_denied','oversized','invalid')),
                PRIMARY KEY(node_id,part),
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((availability_state='available')=(retention_owner_token IS NOT NULL)),
                CHECK(availability_state<>'available' OR (retention_item_id IS NOT NULL AND retention_store_instance_id IS NOT NULL AND source_captured_at IS NOT NULL AND source_expires_at IS NOT NULL AND retention_revision IS NOT NULL AND retention_ownership_receipt IS NOT NULL)),
                CHECK((locator_kind='whole_event' AND json_pointer IS NULL AND part='event_content') OR (locator_kind='json_pointer' AND json_pointer IS NOT NULL AND part<>'event_content'))
            );
            """),
    ];

    private static readonly SqliteOwnedSchemaDefinition V5ContentTombstones = new("table", "local_workspace_content_tombstones", "local_workspace_content_tombstones", """
        CREATE TABLE local_workspace_content_tombstones (
            store_kind TEXT NOT NULL CHECK(store_kind='session_event_content'),
            source_item_id TEXT NOT NULL,
            part TEXT NOT NULL CHECK(part IN ('instruction','tool_input','tool_result','error_message','subagent_input','event_content')),
            locator_kind TEXT NOT NULL CHECK(locator_kind IN ('whole_event','json_pointer')),
            json_pointer TEXT NULL CHECK(json_pointer IS NULL OR json_pointer IN ('/prompt','/tool_input','/tool_response','/error')),
            selected_utf8_bytes INTEGER NULL CHECK(selected_utf8_bytes IS NULL OR selected_utf8_bytes>=0),
            deleted_at TEXT NOT NULL,
            retention_item_id TEXT NOT NULL,
            retention_revision INTEGER NOT NULL CHECK(retention_revision>0),
            PRIMARY KEY(store_kind,source_item_id,part),
            CHECK((locator_kind='whole_event' AND json_pointer IS NULL AND part='event_content') OR (locator_kind='json_pointer' AND json_pointer IS NOT NULL AND part<>'event_content'))
        );
        """);
    private static readonly SqliteOwnedSchemaDefinition V5ContentReferences = new("table", "local_workspace_node_content_refs", "local_workspace_node_content_refs", """
        CREATE TABLE local_workspace_node_content_refs (
            node_id TEXT NOT NULL,
            part TEXT NOT NULL CHECK(part IN ('instruction','tool_input','tool_result','error_message','subagent_input','event_content')),
            store_kind TEXT NOT NULL,
            source_item_id TEXT NOT NULL,
            locator_kind TEXT NOT NULL CHECK(locator_kind IN ('whole_event','json_pointer')),
            json_pointer TEXT NULL CHECK(json_pointer IS NULL OR json_pointer IN ('/prompt','/tool_input','/tool_response','/error')),
            selected_utf8_bytes INTEGER NULL CHECK(selected_utf8_bytes IS NULL OR selected_utf8_bytes>=0),
            revision_input TEXT NOT NULL,
            retention_item_id TEXT NULL,
            retention_store_instance_id TEXT NULL,
            source_captured_at TEXT NULL,
            source_expires_at TEXT NULL,
            retention_revision INTEGER NULL CHECK(retention_revision IS NULL OR retention_revision>0),
            retention_ownership_receipt BLOB NULL CHECK(retention_ownership_receipt IS NULL OR (typeof(retention_ownership_receipt)='blob' AND length(retention_ownership_receipt)=32)),
            retention_owner_token BLOB NULL CHECK(retention_owner_token IS NULL OR (typeof(retention_owner_token)='blob' AND length(retention_owner_token)=32)),
            availability_state TEXT NOT NULL CHECK(availability_state IN ('available','not_captured','expired','deleted','read_denied','oversized','invalid')),
            PRIMARY KEY(node_id,part),
            FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
            CHECK((availability_state='available')=(retention_owner_token IS NOT NULL)),
            CHECK(availability_state<>'available' OR (retention_item_id IS NOT NULL AND retention_store_instance_id IS NOT NULL AND source_captured_at IS NOT NULL AND source_expires_at IS NOT NULL AND retention_revision IS NOT NULL AND retention_ownership_receipt IS NOT NULL)),
            CHECK((locator_kind='whole_event' AND json_pointer IS NULL AND part='event_content') OR (locator_kind='json_pointer' AND json_pointer IS NOT NULL AND part<>'event_content'))
        );
        """);
    private static readonly SqliteOwnedSchemaDefinition V5ExecutionHeaders = new("table", "local_workspace_execution_headers", "local_workspace_execution_headers",
        V4Definitions.Single(static definition => definition.Name == "local_workspace_execution_headers").Sql.Replace(
            "CHECK((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (time_authority='recorded' AND end_utc_ticks>=start_utc_ticks AND duration_ms=(end_utc_ticks-start_utc_ticks)/10000))",
            "CHECK((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (end_utc_ticks IS NOT NULL AND duration_ms IS NOT NULL AND time_authority='recorded' AND start_utc_ticks IS NOT NULL AND end_utc_ticks>=start_utc_ticks AND duration_ms=(end_utc_ticks-start_utc_ticks)/10000)),\n                CHECK((time_authority='recorded' AND ((status='active' AND end_utc_ticks IS NULL AND duration_ms IS NULL) OR (status IN ('completed','failed') AND end_utc_ticks IS NOT NULL AND duration_ms IS NOT NULL) OR (status='unknown' AND ((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (end_utc_ticks IS NOT NULL AND duration_ms IS NOT NULL))))) OR (time_authority<>'recorded' AND end_utc_ticks IS NULL AND duration_ms IS NULL))",
            StringComparison.Ordinal));
    private static readonly SqliteOwnedSchemaDefinition V5Sessions = new("table", "local_workspace_sessions", "local_workspace_sessions",
        V4Sessions.Sql
            .Replace(
                "label_state TEXT NOT NULL,",
                "label_state TEXT NOT NULL CHECK(label_state IN ('recorded','not_observed','not_captured','expired')),",
                StringComparison.Ordinal)
            .Replace(
                "label_expires_at TEXT NULL,",
                "label_expires_at TEXT NULL,\n                label_owner_revision TEXT NULL CHECK(label_owner_revision IS NULL OR (length(label_owner_revision)=64 AND label_owner_revision=lower(label_owner_revision) AND label_owner_revision NOT GLOB '*[^0-9a-f]*')),\n                instruction_count INTEGER NULL CHECK(instruction_count IS NULL OR instruction_count>=1),",
                StringComparison.Ordinal)
            .Replace(
                "CHECK((label_state='recorded' AND label_text IS NOT NULL AND label_source_identity IS NOT NULL AND label_expires_at IS NOT NULL) OR (label_state<>'recorded' AND label_text IS NULL AND label_source_identity IS NULL AND label_expires_at IS NULL))",
                "CHECK((label_state='recorded' AND label_text IS NOT NULL AND label_source_identity IS NOT NULL AND label_expires_at IS NOT NULL AND label_owner_revision IS NOT NULL AND instruction_count IS NOT NULL) OR (label_state<>'recorded' AND label_text IS NULL AND label_source_identity IS NULL AND label_expires_at IS NULL AND label_owner_revision IS NULL AND instruction_count IS NULL))",
                StringComparison.Ordinal)
            .Replace(
                "timing_state TEXT NOT NULL,",
                "timing_state TEXT NOT NULL CHECK(timing_state IN ('recorded','not_observed','inconsistent')),",
                StringComparison.Ordinal)
            .Replace(
                "CHECK((last_seen_at IS NULL)=(last_seen_epoch_ms IS NULL))",
                "CHECK((last_seen_at IS NULL)=(last_seen_epoch_ms IS NULL)),\n                CHECK((timing_state='recorded' AND started_at IS NOT NULL AND last_seen_at IS NOT NULL AND ((status='active' AND ended_at IS NULL AND duration_ms IS NULL) OR (status IN ('completed','failed') AND ended_at IS NOT NULL AND duration_ms IS NOT NULL) OR (status='unknown' AND ((ended_at IS NULL AND duration_ms IS NULL) OR (ended_at IS NOT NULL AND duration_ms IS NOT NULL))))) OR (timing_state<>'recorded' AND duration_ms IS NULL))",
                StringComparison.Ordinal));
    private static readonly SqliteOwnedSchemaDefinition V5Nodes = new("table", "local_workspace_nodes", "local_workspace_nodes",
        V4Definitions.Single(static definition => definition.Name == "local_workspace_nodes").Sql
            .Replace(
                "source_kind IN ('execution_root','session_event','skill_invocation','unknown_relation_group')",
                "source_kind IN ('execution_root','session_event','skill_invocation','semantic_tool','semantic_subagent','unknown_relation_group')",
                StringComparison.Ordinal)
            .Replace(
                "CHECK((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (time_authority='recorded' AND end_utc_ticks>=start_utc_ticks AND duration_ms=(end_utc_ticks-start_utc_ticks)/10000))",
                "CHECK((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (end_utc_ticks IS NOT NULL AND duration_ms IS NOT NULL AND time_authority='recorded' AND start_utc_ticks IS NOT NULL AND end_utc_ticks>=start_utc_ticks AND duration_ms=(end_utc_ticks-start_utc_ticks)/10000)),\n                CHECK((time_authority='recorded' AND ((status='active' AND end_utc_ticks IS NULL AND duration_ms IS NULL) OR (status IN ('completed','failed') AND end_utc_ticks IS NOT NULL AND duration_ms IS NOT NULL) OR (status='unknown' AND ((end_utc_ticks IS NULL AND duration_ms IS NULL) OR (end_utc_ticks IS NOT NULL AND duration_ms IS NOT NULL))))) OR (time_authority<>'recorded' AND start_utc_ticks IS NULL AND end_utc_ticks IS NULL AND duration_ms IS NULL))",
                StringComparison.Ordinal));
    private static readonly SqliteOwnedSchemaDefinition V5NodeEdges = new("table", "local_workspace_node_edges", "local_workspace_node_edges",
        V4Definitions.Single(static definition => definition.Name == "local_workspace_node_edges").Sql.Replace(
            "relation_kind IN ('parent','retry','recovery')",
            "relation_kind='parent'",
            StringComparison.Ordinal));
    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> Definitions =
    [
        V5Sessions,
        .. V4Definitions.Where(static definition => definition.Name is not ("local_workspace_sessions" or "local_workspace_execution_headers" or "local_workspace_executions_by_session" or "local_workspace_nodes" or "local_workspace_nodes_by_parent" or "local_workspace_node_edges" or "local_workspace_content_tombstones" or "local_workspace_node_content_refs")),
        V5ExecutionHeaders,
        V4Definitions.Single(static definition => definition.Name == "local_workspace_executions_by_session"),
        V5Nodes,
        V4Definitions.Single(static definition => definition.Name == "local_workspace_nodes_by_parent"),
        V5NodeEdges,
        V5ContentTombstones,
        V5ContentReferences,
        new("table", "local_workspace_semantic_receipts", "local_workspace_semantic_receipts", """
            CREATE TABLE local_workspace_semantic_receipts (
                node_id TEXT PRIMARY KEY,
                semantic_kind TEXT NOT NULL CHECK(semantic_kind IN ('tool','subagent')),
                source_family TEXT NOT NULL CHECK(source_family IN ('claude_hook','session_sdk','otel')),
                scope_kind TEXT NOT NULL CHECK(scope_kind IN ('native_session','native_run','otel_span')),
                carrier_digest TEXT NOT NULL CHECK(length(carrier_digest)=64 AND carrier_digest NOT GLOB '*[^0-9a-f]*'),
                authority_receipt TEXT NOT NULL CHECK(length(authority_receipt)>0),
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_node_source_references", "local_workspace_node_source_references", """
            CREATE TABLE local_workspace_node_source_references (
                node_id TEXT NOT NULL,
                source_ordinal INTEGER NOT NULL CHECK(source_ordinal BETWEEN 0 AND 15),
                source_kind TEXT NOT NULL CHECK(source_kind IN ('session_run','session_event','otel_span','skill_claim')),
                source_identity TEXT NULL,
                trace_id TEXT NULL,
                span_id TEXT NULL,
                event_id TEXT NULL,
                revision_input TEXT NOT NULL,
                PRIMARY KEY(node_id,source_ordinal),
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK(source_identity IS NOT NULL OR trace_id IS NOT NULL OR span_id IS NOT NULL OR event_id IS NOT NULL),
                CHECK((trace_id IS NULL)=(span_id IS NULL))
            );
            """),
        new("index", "local_workspace_source_references_by_node", "local_workspace_node_source_references", "CREATE INDEX local_workspace_source_references_by_node ON local_workspace_node_source_references(node_id,source_ordinal);"),
        new("table", "local_workspace_tool_metadata", "local_workspace_tool_metadata", """
            CREATE TABLE local_workspace_tool_metadata (
                node_id TEXT PRIMARY KEY,
                caller_state TEXT NOT NULL CHECK(caller_state IN ('recorded','not_observed','source_unsupported','projection_invalid')),
                caller_node_id TEXT NULL,
                started_state TEXT NOT NULL CHECK(started_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                completed_state TEXT NOT NULL CHECK(completed_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                failed_state TEXT NOT NULL CHECK(failed_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                exit_state TEXT NOT NULL CHECK(exit_state IN ('recorded','not_observed','source_unsupported')),
                exit_code INTEGER NULL,
                mcp_server_identity_state TEXT NOT NULL CHECK(mcp_server_identity_state IN ('recorded','not_observed','source_unsupported')),
                mcp_server_identity TEXT NULL,
                mcp_server_name_state TEXT NOT NULL CHECK(mcp_server_name_state IN ('recorded','not_observed','source_unsupported')),
                mcp_server_name TEXT NULL,
                mcp_tool_name_state TEXT NOT NULL CHECK(mcp_tool_name_state IN ('recorded','not_observed','source_unsupported','invalid')),
                mcp_tool_name TEXT NULL,
                retry_state TEXT NOT NULL CHECK(retry_state IN ('recorded','not_observed','source_unsupported')),
                recovery_state TEXT NOT NULL CHECK(recovery_state IN ('recorded','not_observed','source_unsupported')),
                child_activity_state TEXT NOT NULL CHECK(child_activity_state IN ('recorded','not_observed','source_unsupported')),
                child_activity_count INTEGER NULL CHECK(child_activity_count IS NULL OR child_activity_count>=0),
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((caller_state='recorded')=(caller_node_id IS NOT NULL)),
                CHECK((exit_state='recorded')=(exit_code IS NOT NULL)),
                CHECK((mcp_server_identity_state='recorded')=(mcp_server_identity IS NOT NULL)),
                CHECK((mcp_server_name_state='recorded')=(mcp_server_name IS NOT NULL)),
                CHECK((mcp_tool_name_state='recorded')=(mcp_tool_name IS NOT NULL)),
                CHECK((child_activity_state='recorded')=(child_activity_count IS NOT NULL))
            );
            """),
        new("table", "local_workspace_skill_metadata", "local_workspace_skill_metadata", """
            CREATE TABLE local_workspace_skill_metadata (
                node_id TEXT PRIMARY KEY,
                current_valid_state TEXT NOT NULL CHECK(current_valid_state IN ('current','stale','invalid','certification_pending','unavailable')),
                source_state TEXT NOT NULL CHECK(source_state IN ('recorded','not_observed','unavailable')),
                source TEXT NULL,
                trigger_state TEXT NOT NULL CHECK(trigger_state IN ('recorded','not_observed','unavailable')),
                trigger TEXT NULL,
                inventory_reference_state TEXT NOT NULL CHECK(inventory_reference_state='unavailable'),
                inventory_reference TEXT NULL CHECK(inventory_reference IS NULL),
                historical_snapshot_reference_state TEXT NOT NULL CHECK(historical_snapshot_reference_state IN ('recorded','not_observed','unavailable')),
                historical_snapshot_reference TEXT NULL,
                registry_generation_identity TEXT NOT NULL,
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((source_state='recorded')=(source IS NOT NULL)),
                CHECK((trigger_state='recorded')=(trigger IS NOT NULL)),
                CHECK((historical_snapshot_reference_state='recorded')=(historical_snapshot_reference IS NOT NULL))
            );
            """),
        new("table", "local_workspace_subagent_lifecycle", "local_workspace_subagent_lifecycle", """
            CREATE TABLE local_workspace_subagent_lifecycle (
                node_id TEXT PRIMARY KEY,
                selected_state TEXT NOT NULL CHECK(selected_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                started_state TEXT NOT NULL CHECK(started_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                completed_state TEXT NOT NULL CHECK(completed_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                failed_state TEXT NOT NULL CHECK(failed_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                deselected_state TEXT NOT NULL CHECK(deselected_state IN ('recorded','not_observed','source_unsupported','inconsistent')),
                input_state TEXT NOT NULL CHECK(input_state IN ('available','not_captured','expired','deleted','read_denied','oversized','invalid','source_unsupported')),
                FOREIGN KEY(node_id) REFERENCES local_workspace_nodes(node_id) ON UPDATE RESTRICT ON DELETE CASCADE
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
    internal static IEnumerable<string> ExactV4SchemaSql => V4Definitions.Select(static definition => definition.Sql);

    internal static void Ensure(SqliteConnection connection, DateTimeOffset now)
    {
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction, now, null, null, null);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, DateTimeOffset now, Action beforeV4Stamp)
    {
        ArgumentNullException.ThrowIfNull(beforeV4Stamp);
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction, now, null, beforeV4Stamp, null);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, DateTimeOffset now, Action<string> migrationCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(migrationCheckpoint);
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction, now, null, null, migrationCheckpoint);
        transaction.Commit();
    }

    internal static void Ensure(
        SqliteConnection connection,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority skillRegistryAuthority)
    {
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction, now, skillRegistryAuthority, null, null);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
        => Ensure(connection, transaction, now, null, null, null);

    internal static void Ensure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority)
        => Ensure(connection, transaction, now, skillRegistryAuthority, null, null);

    private static void Ensure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority,
        Action? beforeV4Stamp,
        Action<string>? migrationCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
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
            Execute(connection, transaction, "UPDATE schema_version SET version=3 WHERE component='local_workspace_projection' AND version=2;");
            version = 3;
            owned = ReadOwnedObjects(connection, transaction);
        }
        if (version == 3 && SqliteOwnedSchemaAuthority.Equal(owned, SqliteOwnedSchemaAuthority.Compile(V3Definitions)))
        {
            foreach (var table in V3Definitions.Where(static definition => definition.Type == "table").Select(static definition => definition.Name).Reverse())
                Execute(connection, transaction, $"DROP TABLE {table};");
            foreach (var definition in V4Definitions) Execute(connection, transaction, definition.Sql);
            BackfillSpanFacts(connection, transaction);
            RefreshProjection(connection, transaction, now, skillRegistryAuthority);
            ValidateSemanticRows(connection, transaction);
            beforeV4Stamp?.Invoke();
            Execute(connection, transaction, "UPDATE schema_version SET version=4 WHERE component='local_workspace_projection' AND version=3;");
            version = 4;
            owned = ReadOwnedObjects(connection, transaction);
        }
        if (version == 4 && SqliteOwnedSchemaAuthority.Equal(owned, SqliteOwnedSchemaAuthority.Compile(V4Definitions)))
        {
            ValidateSemanticRows(connection, transaction);
            var terminalAuthority = LocalWorkspaceTerminalAuthority.Capture(connection, transaction);
            migrationCheckpoint?.Invoke("after_validate");
            Execute(connection, transaction, """
                CREATE TEMP TABLE local_workspace_v4_tombstones AS
                SELECT 'session_event_content' store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision
                FROM local_workspace_content_tombstones
                WHERE part<>'subagent_input' AND NOT (json_pointer IS '/agent_id');
                """);
            foreach (var table in V4Definitions.Where(static definition => definition.Type == "table").Select(static definition => definition.Name).Reverse())
                Execute(connection, transaction, $"DROP TABLE {table};");
            foreach (var definition in Definitions) Execute(connection, transaction, definition.Sql);
            Execute(connection, transaction, """
                INSERT INTO local_workspace_content_tombstones(store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision)
                SELECT store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision
                FROM local_workspace_v4_tombstones ORDER BY store_kind,source_item_id,part;
                DROP TABLE local_workspace_v4_tombstones;
                """);
            migrationCheckpoint?.Invoke("after_rebuild");
            BackfillSpanFacts(connection, transaction);
            migrationCheckpoint?.Invoke("after_backfill");
            RefreshProjection(connection, transaction, now, skillRegistryAuthority);
            terminalAuthority.ApplyReadDenied(connection, transaction);
            migrationCheckpoint?.Invoke("after_refresh");
            ValidateSemanticRows(connection, transaction);
            migrationCheckpoint?.Invoke("after_semantic_validation");
            migrationCheckpoint?.Invoke("before_stamp");
            Execute(connection, transaction, "UPDATE schema_version SET version=5 WHERE component='local_workspace_projection' AND version=4;");
            migrationCheckpoint?.Invoke("after_stamp_before_commit");
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
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',5);");
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
            throw new InvalidOperationException("Unsupported incomplete local_workspace_projection schema version 5.");
    }

    internal static void ValidateAndRestampCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        Validate(connection, transaction);
        ValidateSemanticRows(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE schema_version SET version=5 WHERE component='local_workspace_projection' AND version=5;";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("local_workspace_projection_component_stamp_invalid");
    }

    internal static void ValidateCurrentOrExactLegacy(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = ReadVersion(connection, transaction);
        var owned = ReadOwnedObjects(connection, transaction);
        if (version == Version && SqliteOwnedSchemaAuthority.Equal(owned, ExpectedObjects)) return;
        if (version == 4 && SqliteOwnedSchemaAuthority.Equal(owned, SqliteOwnedSchemaAuthority.Compile(V4Definitions))) return;
        if (version == 3 && SqliteOwnedSchemaAuthority.Equal(owned, SqliteOwnedSchemaAuthority.Compile(V3Definitions))) return;
        if (version == 2 && SqliteOwnedSchemaAuthority.Equal(owned, V2ExpectedObjects)) return;
        if (version == 1 && SqliteOwnedSchemaAuthority.Equal(owned, V1ExpectedObjects)) return;
        throw new InvalidOperationException("Unsupported incomplete local_workspace_projection schema.");
    }

    internal static void ValidateSemanticRows(SqliteConnection connection, SqliteTransaction transaction)
    {
        var v5Validation = TableExists(connection, transaction, "local_workspace_semantic_receipts")
            ? """
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n LEFT JOIN local_workspace_semantic_receipts r ON r.node_id=n.node_id
                     WHERE n.source_kind IN ('semantic_tool','semantic_subagent') AND (r.node_id IS NULL OR r.carrier_digest<>n.source_identity
                       OR r.semantic_kind<>CASE n.source_kind WHEN 'semantic_tool' THEN 'tool' ELSE 'subagent' END))
              OR EXISTS(SELECT 1 FROM local_workspace_semantic_receipts r JOIN local_workspace_nodes n ON n.node_id=r.node_id
                     WHERE n.source_kind NOT IN ('semantic_tool','semantic_subagent')
                        OR (SELECT COUNT(*) FROM local_workspace_node_source_references x WHERE x.node_id=r.node_id) NOT BETWEEN 1 AND 16)
              OR EXISTS(SELECT 1 FROM local_workspace_node_source_references r
                     WHERE r.source_kind='session_run' AND NOT EXISTS(SELECT 1 FROM session_runs x WHERE x.run_id=r.source_identity)
                        OR r.source_kind='session_event' AND NOT EXISTS(SELECT 1 FROM session_events x WHERE x.event_id=r.event_id AND x.event_id=r.source_identity))
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n LEFT JOIN local_workspace_tool_metadata m ON m.node_id=n.node_id
                     WHERE n.source_kind='semantic_tool' AND m.node_id IS NULL)
              OR EXISTS(SELECT 1 FROM local_workspace_tool_metadata m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.source_kind<>'semantic_tool')
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n LEFT JOIN local_workspace_subagent_lifecycle m ON m.node_id=n.node_id
                     WHERE n.source_kind='semantic_subagent' AND m.node_id IS NULL)
              OR EXISTS(SELECT 1 FROM local_workspace_subagent_lifecycle m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.source_kind<>'semantic_subagent')
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n LEFT JOIN local_workspace_skill_metadata m ON m.node_id=n.node_id
                     WHERE n.source_kind='skill_invocation' AND m.node_id IS NULL)
              OR EXISTS(SELECT 1 FROM local_workspace_skill_metadata m JOIN local_workspace_nodes n ON n.node_id=m.node_id WHERE n.source_kind<>'skill_invocation')
              """
            : string.Empty;
        var contentBindingValidation = TableExists(connection, transaction, "local_workspace_node_source_references")
            ? """
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id
                     WHERE c.store_kind<>'session_event_content' OR NOT (
                       (n.source_kind='session_event' AND n.source_identity=c.source_item_id)
                       OR (n.source_kind='semantic_tool' AND EXISTS(SELECT 1 FROM local_workspace_node_source_references r WHERE r.node_id=n.node_id AND r.event_id=c.source_item_id))))
              """
            : "OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE c.store_kind<>'session_event_content' OR n.source_kind<>'session_event' OR n.source_identity<>c.source_item_id)";
        var spanFactValidation = TableExists(connection, transaction, "monitor_spans")
            ? "OR EXISTS(SELECT 1 FROM local_workspace_span_facts f LEFT JOIN monitor_spans s ON s.raw_record_id=f.raw_record_id AND s.span_ordinal=f.span_ordinal WHERE s.raw_record_id IS NULL)"
            : "OR EXISTS(SELECT 1 FROM local_workspace_span_facts)";
        var contentStoreKindInstalled = ColumnExists(connection, transaction, "local_workspace_node_content_refs", "store_kind");
        var tombstoneStoreKindInstalled = ColumnExists(connection, transaction, "local_workspace_content_tombstones", "store_kind");
        var retentionAuthorityInstalled = TableExists(connection, transaction, "retention_items")
            && TableExists(connection, transaction, "retention_store_instances")
            && TableExists(connection, transaction, "retention_tombstones");
        var readDeniedValidation = contentStoreKindInstalled && retentionAuthorityInstalled
            ? $$"""
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c
                     LEFT JOIN session_events e ON e.event_id=c.source_item_id
                     LEFT JOIN session_event_content s ON s.event_id=c.source_item_id
                     LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
                     WHERE c.availability_state='read_denied' AND (
                       c.store_kind<>'session_event_content' OR e.event_id IS NULL OR i.item_id IS NULL
                       OR i.store_kind IS NOT c.store_kind OR i.source_item_id IS NOT c.source_item_id
                       OR i.store_instance_id IS NOT c.retention_store_instance_id
                       OR i.store_instance_id IS NOT (SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                       OR i.captured_at IS NOT c.source_captured_at OR i.expires_at IS NOT c.source_expires_at
                       OR i.revision IS NOT c.retention_revision OR i.ownership_receipt IS NOT c.retention_ownership_receipt
                       OR c.retention_owner_token IS NOT NULL OR i.read_denied_at IS NULL OR i.state='deleted'
                       OR i.deleted_at IS NOT NULL OR i.error_code IS NOT NULL
                       OR EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id)
                       OR (s.event_id IS NOT NULL AND (s.captured_at IS NOT i.captured_at OR s.expires_at IS NOT i.expires_at
                         OR local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,s.content_kind,s.captured_at,s.expires_at,
                           e.session_id,e.run_id,e.source_adapter,e.source_event_id,s.retention_owner_token,i.ownership_receipt)<>1))
                       OR c.revision_input<>e.content_state||'|'||i.captured_at||'|'||i.expires_at||'|'||i.item_id||'|'||i.store_instance_id||'|'||CAST(i.revision AS TEXT)||'|'||i.state||'|'
                     ))
              """
            : "OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs WHERE availability_state='read_denied')";
        var retentionValidation = contentStoreKindInstalled && tombstoneStoreKindInstalled && retentionAuthorityInstalled
            ? """
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c
                     LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
                     WHERE c.retention_item_id IS NOT NULL AND c.availability_state<>'deleted'
                       AND (i.item_id IS NULL OR i.store_kind IS NOT c.store_kind OR i.source_item_id IS NOT c.source_item_id
                         OR i.store_instance_id IS NOT c.retention_store_instance_id OR i.revision IS NOT c.retention_revision
                         OR i.ownership_receipt IS NOT c.retention_ownership_receipt))
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c
                     LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
                     LEFT JOIN local_workspace_content_tombstones x ON x.store_kind=c.store_kind AND x.source_item_id=c.source_item_id AND x.part=c.part
                     WHERE c.availability_state='deleted' AND (c.retention_owner_token IS NOT NULL OR i.item_id IS NULL
                       OR c.retention_item_id IS NOT x.retention_item_id OR c.retention_revision IS NOT x.retention_revision
                       OR x.source_item_id IS NULL OR x.locator_kind<>c.locator_kind OR NOT (x.json_pointer IS c.json_pointer)
                       OR NOT (x.selected_utf8_bytes IS c.selected_utf8_bytes)
                       OR NOT (i.state='deleted' OR i.deleted_at IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id))))
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c
                     JOIN local_workspace_nodes n ON n.node_id=c.node_id LEFT JOIN session_events e ON e.event_id=c.source_item_id LEFT JOIN session_event_content s ON s.event_id=e.event_id
                     LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
                     WHERE c.availability_state='available' AND (e.event_id IS NULL OR s.event_id IS NULL
                       OR i.store_instance_id<>c.retention_store_instance_id OR i.store_instance_id<>(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                       OR i.store_kind<>'session_event_content' OR i.source_item_id<>c.source_item_id OR i.captured_at<>c.source_captured_at OR i.expires_at<>c.source_expires_at
                       OR i.revision<>c.retention_revision OR i.ownership_receipt<>c.retention_ownership_receipt OR s.captured_at<>c.source_captured_at OR s.expires_at<>c.source_expires_at
                       OR s.retention_owner_token<>c.retention_owner_token OR local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,s.content_kind,s.captured_at,s.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,s.retention_owner_token,i.ownership_receipt)<>1
                       OR i.read_denied_at IS NOT NULL OR i.deleted_at IS NOT NULL OR i.error_code IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id)))
              OR EXISTS(SELECT 1 FROM local_workspace_content_tombstones x
                     LEFT JOIN session_events e ON e.event_id=x.source_item_id
                     LEFT JOIN retention_items i ON i.item_id=x.retention_item_id AND i.store_kind='session_event_content' AND i.source_item_id=x.source_item_id
                     LEFT JOIN local_workspace_node_content_refs c ON c.store_kind=x.store_kind AND c.source_item_id=x.source_item_id AND c.part=x.part
                     WHERE e.event_id IS NULL OR i.item_id IS NULL OR i.revision<>x.retention_revision
                       OR NOT (i.state='deleted' OR i.deleted_at IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones rt WHERE rt.item_id=i.item_id))
                       OR c.node_id IS NULL OR c.availability_state<>'deleted' OR c.locator_kind<>x.locator_kind
                       OR NOT (c.json_pointer IS x.json_pointer) OR NOT (c.selected_utf8_bytes IS x.selected_utf8_bytes)
                       OR c.retention_owner_token IS NOT NULL)
              """
            : contentStoreKindInstalled && !tombstoneStoreKindInstalled && retentionAuthorityInstalled
            ? """
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c
                     LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
                     WHERE c.retention_item_id IS NOT NULL AND c.availability_state<>'deleted'
                       AND (i.item_id IS NULL OR i.store_kind IS NOT c.store_kind OR i.source_item_id IS NOT c.source_item_id
                         OR i.store_instance_id IS NOT c.retention_store_instance_id OR i.revision IS NOT c.retention_revision
                         OR i.ownership_receipt IS NOT c.retention_ownership_receipt))
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c
                     LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
                     LEFT JOIN local_workspace_content_tombstones x ON x.source_item_id=c.source_item_id AND x.part=c.part
                     WHERE c.availability_state='deleted' AND (c.store_kind<>'session_event_content' OR c.retention_owner_token IS NOT NULL OR i.item_id IS NULL
                       OR c.retention_item_id IS NOT x.retention_item_id OR c.retention_revision IS NOT x.retention_revision
                       OR x.source_item_id IS NULL OR x.locator_kind<>c.locator_kind OR NOT (x.json_pointer IS c.json_pointer)
                       OR NOT (x.selected_utf8_bytes IS c.selected_utf8_bytes)
                       OR NOT (i.state='deleted' OR i.deleted_at IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id))))
              OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs c
                     JOIN local_workspace_nodes n ON n.node_id=c.node_id LEFT JOIN session_events e ON e.event_id=c.source_item_id LEFT JOIN session_event_content s ON s.event_id=e.event_id
                     LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
                     WHERE c.availability_state='available' AND (c.store_kind<>'session_event_content' OR e.event_id IS NULL OR s.event_id IS NULL OR i.item_id IS NULL
                       OR i.store_instance_id<>c.retention_store_instance_id OR i.store_instance_id<>(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                       OR i.store_kind<>'session_event_content' OR i.source_item_id<>c.source_item_id OR i.captured_at<>c.source_captured_at OR i.expires_at<>c.source_expires_at
                       OR i.revision<>c.retention_revision OR i.ownership_receipt<>c.retention_ownership_receipt OR s.captured_at<>c.source_captured_at OR s.expires_at<>c.source_expires_at
                       OR s.retention_owner_token<>c.retention_owner_token OR local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,s.content_kind,s.captured_at,s.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,s.retention_owner_token,i.ownership_receipt)<>1
                       OR i.read_denied_at IS NOT NULL OR i.deleted_at IS NOT NULL OR i.error_code IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id)))
              OR EXISTS(SELECT 1 FROM local_workspace_content_tombstones x
                     LEFT JOIN session_events e ON e.event_id=x.source_item_id
                     LEFT JOIN retention_items i ON i.item_id=x.retention_item_id AND i.store_kind='session_event_content' AND i.source_item_id=x.source_item_id
                     LEFT JOIN local_workspace_node_content_refs c ON c.store_kind='session_event_content' AND c.source_item_id=x.source_item_id AND c.part=x.part
                     WHERE e.event_id IS NULL OR i.item_id IS NULL OR i.revision<>x.retention_revision
                       OR NOT (i.state='deleted' OR i.deleted_at IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones rt WHERE rt.item_id=i.item_id))
                       OR c.node_id IS NULL OR c.availability_state<>'deleted' OR c.locator_kind<>x.locator_kind
                       OR NOT (c.json_pointer IS x.json_pointer) OR NOT (c.selected_utf8_bytes IS x.selected_utf8_bytes)
                       OR c.retention_owner_token IS NOT NULL)
              """
            : "OR EXISTS(SELECT 1 FROM local_workspace_node_content_refs WHERE availability_state='available')";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
              EXISTS(SELECT 1 FROM local_workspace_sessions s
                     WHERE s.label_state NOT IN ('recorded','not_observed','not_captured','expired')
                        OR s.timing_state NOT IN ('recorded','not_observed','inconsistent')
                        OR NOT ((s.timing_state='recorded' AND s.started_at IS NOT NULL AND s.last_seen_at IS NOT NULL
                          AND ((s.status='active' AND s.ended_at IS NULL AND s.duration_ms IS NULL)
                            OR (s.status IN ('completed','failed') AND s.ended_at IS NOT NULL AND s.duration_ms IS NOT NULL)
                            OR (s.status='unknown' AND ((s.ended_at IS NULL AND s.duration_ms IS NULL) OR (s.ended_at IS NOT NULL AND s.duration_ms IS NOT NULL)))))
                          OR (s.timing_state<>'recorded' AND s.duration_ms IS NULL)))
              OR EXISTS(SELECT 1 FROM local_workspace_execution_headers h
                     LEFT JOIN session_runs r ON r.run_id=h.source_identity AND r.session_id=h.session_id
                     WHERE r.run_id IS NULL)
              OR EXISTS(SELECT 1 FROM local_workspace_execution_headers h
                     WHERE h.execution_id<>local_workspace_execution_id(h.source_kind,h.source_identity)
                        OR NOT ((h.time_authority='recorded' AND ((h.status='active' AND h.end_utc_ticks IS NULL AND h.duration_ms IS NULL)
                          OR (h.status IN ('completed','failed') AND h.end_utc_ticks IS NOT NULL AND h.duration_ms IS NOT NULL)
                          OR (h.status='unknown' AND ((h.end_utc_ticks IS NULL AND h.duration_ms IS NULL) OR (h.end_utc_ticks IS NOT NULL AND h.duration_ms IS NOT NULL)))))
                          OR (h.time_authority<>'recorded' AND h.end_utc_ticks IS NULL AND h.duration_ms IS NULL))
                        OR (h.end_utc_ticks IS NULL)<>(h.duration_ms IS NULL)
                        OR (h.end_utc_ticks IS NOT NULL AND (h.duration_ms IS NULL OR h.time_authority<>'recorded' OR h.start_utc_ticks IS NULL
                          OR h.end_utc_ticks<h.start_utc_ticks OR h.duration_ms<>(h.end_utc_ticks-h.start_utc_ticks)/10000)))
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n
                     JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id
                     WHERE n.source_kind='execution_root' AND n.source_identity<>h.source_identity)
              OR EXISTS(SELECT 1 FROM local_workspace_execution_headers h
                     WHERE (SELECT COUNT(*) FROM local_workspace_nodes n
                            WHERE n.execution_id=h.execution_id AND n.source_kind='execution_root'
                              AND n.source_identity=h.source_identity)<>1)
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n
                     JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id
                     LEFT JOIN session_events e ON e.event_id=n.source_identity
                       AND e.session_id=n.session_id AND e.run_id=h.source_identity
                     WHERE n.source_kind='session_event' AND e.event_id IS NULL)
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n
                     WHERE n.node_id<>local_workspace_node_id(n.source_kind,n.source_identity)
                        OR NOT EXISTS(SELECT 1 FROM local_workspace_execution_headers h WHERE h.execution_id=n.execution_id AND h.session_id=n.session_id)
                        OR (n.parent_node_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM local_workspace_nodes p WHERE p.node_id=n.parent_node_id AND p.execution_id=n.execution_id))
                        OR NOT ((n.time_authority='recorded' AND ((n.status='active' AND n.end_utc_ticks IS NULL AND n.duration_ms IS NULL)
                          OR (n.status IN ('completed','failed') AND n.end_utc_ticks IS NOT NULL AND n.duration_ms IS NOT NULL)
                          OR (n.status='unknown' AND ((n.end_utc_ticks IS NULL AND n.duration_ms IS NULL) OR (n.end_utc_ticks IS NOT NULL AND n.duration_ms IS NOT NULL)))))
                          OR (n.time_authority<>'recorded' AND n.start_utc_ticks IS NULL AND n.end_utc_ticks IS NULL AND n.duration_ms IS NULL))
                        OR (n.end_utc_ticks IS NULL)<>(n.duration_ms IS NULL)
                        OR (n.end_utc_ticks IS NOT NULL AND (n.duration_ms IS NULL OR n.time_authority<>'recorded' OR n.start_utc_ticks IS NULL
                          OR n.end_utc_ticks<n.start_utc_ticks OR n.duration_ms<>(n.end_utc_ticks-n.start_utc_ticks)/10000)))
              OR EXISTS(SELECT 1 FROM local_workspace_node_edges e
                     WHERE e.relation_kind<>'parent'
                        OR NOT EXISTS(SELECT 1 FROM local_workspace_nodes n JOIN local_workspace_nodes r ON r.node_id=e.related_node_id AND r.execution_id=n.execution_id WHERE n.node_id=e.node_id)
                        OR (e.relation_kind='parent' AND NOT EXISTS(SELECT 1 FROM local_workspace_nodes n WHERE n.node_id=e.node_id
                          AND n.parent_node_id=e.related_node_id AND n.relationship_authority=e.relationship_authority)))
              OR EXISTS(SELECT 1 FROM local_workspace_nodes n
                     WHERE n.parent_node_id IS NOT NULL AND n.relationship_authority IN ('exact','explicit')
                       AND NOT EXISTS(SELECT 1 FROM local_workspace_node_edges e WHERE e.node_id=n.node_id
                         AND e.related_node_id=n.parent_node_id AND e.relation_kind='parent'
                         AND e.relationship_authority=n.relationship_authority))
              {contentBindingValidation}
              {retentionValidation}
              {readDeniedValidation}
              OR EXISTS(SELECT 1 FROM local_workspace_nodes WHERE source_kind='skill_invocation' AND otel_source_identity IS NULL AND sdk_source_identity IS NULL)
              {v5Validation}
              {spanFactValidation}
              OR EXISTS(SELECT 1 FROM local_workspace_execution_headers GROUP BY session_id HAVING COUNT(*)>257)
              OR EXISTS(SELECT 1 FROM local_workspace_nodes GROUP BY session_id HAVING COUNT(*)>4097)
              OR EXISTS(SELECT 1 FROM local_workspace_sessions s WHERE s.node_overflow=0 AND (SELECT COUNT(*) FROM local_workspace_nodes n WHERE n.session_id=s.session_id)>4096);
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("local_workspace_projection_semantic_validation_failed");
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table.Replace("'", "''", StringComparison.Ordinal)}') WHERE name=$column);";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
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

        long lastRawRecordId = long.MinValue;
        while (true)
        {
            using (var candidate = connection.CreateCommand())
            {
                candidate.Transaction = transaction;
                candidate.CommandText = "SELECT id,typeof(payload_json),length(CAST(payload_json AS BLOB)) FROM raw_records WHERE id>$after ORDER BY id LIMIT 1;";
                candidate.Parameters.AddWithValue("$after", lastRawRecordId);
                using var reader = candidate.ExecuteReader();
                if (!reader.Read()) break;
                lastRawRecordId = reader.GetInt64(0);
                if (reader.GetString(1) != "text" || reader.IsDBNull(2)
                    || reader.GetInt64(2) is < 1 or > RawReplayLimits.MaximumRawRecordBytes)
                    throw new InvalidOperationException("local_workspace_projection_raw_payload_invalid");
            }

            RawTelemetryRecord record;
            using (var records = connection.CreateCommand())
            {
                records.Transaction = transaction;
                records.CommandText = "SELECT source,trace_id,received_at,resource_attributes_json,payload_json,schema_version FROM raw_records WHERE id=$id AND typeof(payload_json)='text' AND length(CAST(payload_json AS BLOB)) BETWEEN 1 AND $maximum;";
                records.Parameters.AddWithValue("$id", lastRawRecordId);
                records.Parameters.AddWithValue("$maximum", RawReplayLimits.MaximumRawRecordBytes);
                using var reader = records.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("local_workspace_projection_raw_payload_invalid");
                if (!DateTimeOffset.TryParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var receivedAt))
                    throw new InvalidOperationException("local_workspace_projection_raw_timestamp_invalid");
                record = new RawTelemetryRecord(lastRawRecordId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), receivedAt,
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt32(5));
            }

            var facts = MonitorSpanProjectionBuilder.Build(record)
                .Select(span => new { raw = record.Id, ordinal = span.SpanOrdinal, retry = span.RetryCount, total = span.ProducerTotalTokens })
                .ToArray();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM local_workspace_span_facts WHERE raw_record_id=$raw_record_id;";
                delete.Parameters.AddWithValue("$raw_record_id", record.Id);
                delete.ExecuteNonQuery();
            }
            if (facts.Length == 0) continue;
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
}

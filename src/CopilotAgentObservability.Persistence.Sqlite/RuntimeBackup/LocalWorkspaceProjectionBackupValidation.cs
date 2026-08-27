using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal static class LocalWorkspaceProjectionBackupValidation
{
    internal static void Validate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset? publicationTime = null,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority = null,
        SqliteConnection? canonicalReplica = null)
    {
        try
        {
            LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
            LocalWorkspaceProjectionSchemaV1.Validate(connection, transaction);
            LocalWorkspaceProjectionSchemaV1.ValidateSemanticRows(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(
                  SELECT 1 FROM local_workspace_sessions p
                  LEFT JOIN sessions s ON s.session_id=p.session_id
                  WHERE s.session_id IS NULL
                     OR length(p.revision_seed)=0
                     OR (p.sort_group=1 AND p.sort_epoch_ms<>0)
                     OR (p.last_seen_at IS NULL)<>(p.last_seen_epoch_ms IS NULL)
                     OR (SELECT COUNT(*) FROM local_workspace_session_sources x WHERE x.session_id=p.session_id)>5
                     OR (SELECT COUNT(*) FROM local_workspace_session_models x WHERE x.session_id=p.session_id)>16
                     OR EXISTS(SELECT 1 FROM local_workspace_session_sources x WHERE x.session_id=p.session_id AND x.source NOT IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code'))
                     OR EXISTS(SELECT 1 FROM local_workspace_session_models x WHERE x.session_id=p.session_id AND NOT EXISTS(SELECT 1 FROM session_runs r WHERE r.session_id=p.session_id AND r.model=x.model COLLATE BINARY))
                     OR EXISTS(SELECT 1 FROM local_workspace_session_sources x WHERE x.session_id=p.session_id AND NOT EXISTS(
                       SELECT 1 FROM session_native_ids n WHERE n.session_id=p.session_id AND n.source_surface=x.source COLLATE BINARY
                       UNION ALL SELECT 1 FROM session_runs r WHERE r.session_id=p.session_id AND r.source_surface=x.source COLLATE BINARY
                       UNION ALL SELECT 1 FROM session_events e WHERE e.session_id=p.session_id AND e.source_surface=x.source COLLATE BINARY))
                     OR (p.source_state='recorded')<>(EXISTS(SELECT 1 FROM local_workspace_session_sources x WHERE x.session_id=p.session_id))
                     OR (p.model_state='recorded')<>(EXISTS(SELECT 1 FROM local_workspace_session_models x WHERE x.session_id=p.session_id))
                     OR (SELECT COUNT(*) FROM local_workspace_session_activity x WHERE x.session_id=p.session_id)<>5
                     OR p.label_state NOT IN ('recorded','not_observed','not_captured','expired')
                     OR p.timing_state NOT IN ('recorded','not_observed','inconsistent')
                     OR p.label_state='recorded' AND NOT EXISTS(
                       SELECT 1 FROM session_events e
                       JOIN session_event_content c ON c.event_id=e.event_id
                       WHERE e.event_id=p.label_source_identity COLLATE BINARY
                         AND e.session_id=p.session_id COLLATE BINARY
                         AND e.type IN ('user.message','UserPromptSubmit','userPromptSubmitted')
                         AND e.content_state='available'
                         AND c.expires_at=p.label_expires_at COLLATE BINARY)
                     OR EXISTS(SELECT 1 FROM local_workspace_session_activity a WHERE a.session_id=p.session_id AND a.state NOT IN ('recorded','not_observed','capture_gap','source_unsupported','certification_pending','projection_invalid'))
                );
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException();
            command.CommandText = "SELECT label_expires_at FROM local_workspace_sessions WHERE label_state='recorded';";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var value = reader.GetString(0);
                    if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
                        || expiry.Offset != TimeSpan.Zero)
                        throw new InvalidOperationException();
                }
            }
            command.CommandText = "SELECT last_seen_at,last_seen_epoch_ms FROM local_workspace_sessions WHERE last_seen_at IS NOT NULL;";
            using (var timing = command.ExecuteReader())
            {
                while (timing.Read())
                {
                    if (!DateTimeOffset.TryParseExact(timing.GetString(0), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
                        || instant.Offset != TimeSpan.Zero || instant.ToUnixTimeMilliseconds() != timing.GetInt64(1))
                        throw new InvalidOperationException();
                }
            }
            command.CommandText = "SELECT normalized_text FROM local_workspace_session_search_facts;";
            using (var facts = command.ExecuteReader())
                while (facts.Read())
                {
                    var value = facts.GetString(0);
                    if (value.Length == 0 || !string.Equals(value, value.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), StringComparison.Ordinal))
                        throw new InvalidOperationException();
                }
            command.CommandText = "SELECT capture_notes FROM local_workspace_sessions;";
            using (var notes = command.ExecuteReader())
            {
                while (notes.Read())
                {
                    var persisted = notes.GetString(0);
                    var parts = persisted.Length == 0 ? Array.Empty<string>() : persisted.Split(',', StringSplitOptions.None);
                    if (!string.Equals(persisted, LocalWorkspaceProjectionStore.CanonicalizeCaptureNotes(parts), StringComparison.Ordinal)
                        || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length)
                        throw new InvalidOperationException();
                }
            }
            ValidateDurableSemanticGraph(connection, transaction);
            ValidateSdkFactGraph(connection, transaction, publicationTime);
            ValidateCanonicalProjection(connection, transaction, publicationTime, skillRegistryAuthority, canonicalReplica);
            ValidateSpanFacts(connection, transaction);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("local_workspace_projection_backup_invalid", exception);
        }
    }

    private static void ValidateDurableSemanticGraph(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureSkillOwnerCompileShapes(connection, transaction);
        using (var monitorOwner = connection.CreateCommand())
        {
            monitorOwner.Transaction = transaction;
            monitorOwner.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='monitor_spans');";
            if (Convert.ToInt64(monitorOwner.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                monitorOwner.CommandText = "CREATE TEMP TABLE IF NOT EXISTS monitor_spans(trace_id TEXT,span_id TEXT,operation TEXT,category TEXT,status TEXT,start_time TEXT,end_time TEXT);";
                monitorOwner.ExecuteNonQuery();
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1
              FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
              WHERE node.source_kind IS NOT CASE receipt.semantic_kind WHEN 'tool' THEN 'semantic_tool' WHEN 'subagent' THEN 'semantic_subagent' END
                  OR node.kind IS NOT receipt.semantic_kind
                  OR receipt.source_family NOT IN ('session_sdk','otel')
                  OR receipt.carrier_digest IS NOT node.source_identity
                  OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=receipt.node_id) NOT BETWEEN 1 AND 16
                  OR receipt.source_family='session_sdk' AND (
                    receipt.scope_kind IS NOT 'native_run'
                    OR receipt.semantic_kind NOT IN ('tool','subagent')
                    OR receipt.authority_receipt IS NOT CASE receipt.semantic_kind WHEN 'tool' THEN 'copilot-sdk-stream|exact_sdk_tool|v1' ELSE 'copilot-sdk-stream|native_run|v1' END
                    OR EXISTS(
                      SELECT 1 FROM local_workspace_node_source_references reference
                      LEFT JOIN session_events event ON event.event_id=reference.event_id
                      LEFT JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id
                      LEFT JOIN session_runs run ON run.session_id=event.session_id AND run.run_id=event.run_id
                      WHERE reference.node_id=receipt.node_id AND (
                        reference.source_kind IS NOT 'session_event'
                        OR reference.source_identity IS NOT reference.event_id
                        OR event.event_id IS NULL OR event.event_id IS NOT reference.event_id
                        OR event.session_id IS NOT node.session_id
                        OR event.source_surface IS NOT 'copilot-sdk'
                        OR event.source_adapter IS NOT 'copilot-sdk-stream'
                        OR event.source_event_id IS NULL OR length(event.source_event_id)=0
                        OR execution.session_id IS NOT node.session_id OR execution.source_kind IS NOT 'session_run'
                        OR execution.source_identity IS NOT event.run_id
                        OR run.source_surface IS NOT 'copilot-sdk'
                        OR run.native_run_id IS NULL OR length(run.native_run_id)=0
                        OR (SELECT COUNT(*) FROM session_runs candidate
                              WHERE candidate.session_id=event.session_id AND candidate.source_surface='copilot-sdk' COLLATE BINARY
                                AND candidate.native_run_id=run.native_run_id COLLATE BINARY)<>1
                        OR reference.revision_input IS NOT event.source_adapter||'|'||event.source_event_id||'|'||event.type||'|'||event.type||'|1|'||event.occurred_at||'|'||receipt.authority_receipt))
                    OR receipt.semantic_kind='tool' AND (
                      (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id
                         WHERE reference.node_id=receipt.node_id AND event.type='tool.execution_start')<>1
                      OR EXISTS(SELECT 1 FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id
                         WHERE reference.node_id=receipt.node_id AND event.type NOT IN ('tool.execution_start','tool.execution_complete'))
                      OR EXISTS(SELECT 1 FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id
                         WHERE reference.node_id=receipt.node_id AND event.type='tool.execution_complete'
                           AND event.parent_event_id IS NOT (SELECT start.event_id FROM local_workspace_node_source_references anchor
                             JOIN session_events start ON start.event_id=anchor.event_id
                             WHERE anchor.node_id=receipt.node_id AND start.type='tool.execution_start'))
                      OR receipt.carrier_digest IS NOT (
                         SELECT local_workspace_semantic_digest('session_sdk_tool',run.native_run_id,start.source_event_id)
                         FROM local_workspace_node_source_references anchor
                         JOIN session_events start ON start.event_id=anchor.event_id
                         JOIN session_runs run ON run.session_id=start.session_id AND run.run_id=start.run_id
                         WHERE anchor.node_id=receipt.node_id AND start.type='tool.execution_start')
                      OR NOT EXISTS(SELECT 1 FROM local_workspace_tool_metadata metadata WHERE metadata.node_id=receipt.node_id)
                      OR EXISTS(SELECT 1 FROM local_workspace_tool_metadata metadata WHERE metadata.node_id=receipt.node_id AND (
                         metadata.started_state IS NOT 'recorded'
                         OR metadata.completed_state IS NOT CASE
                           WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id
                             WHERE reference.node_id=receipt.node_id AND event.type='tool.execution_complete')>1 THEN 'inconsistent'
                           WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id
                             WHERE reference.node_id=receipt.node_id AND event.type='tool.execution_complete')=1 THEN 'recorded' ELSE 'not_observed' END
                         OR metadata.failed_state IS NOT 'not_observed')))
                    OR receipt.semantic_kind='subagent' AND (
                      EXISTS(SELECT 1 FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id
                        WHERE reference.node_id=receipt.node_id AND event.type NOT IN ('subagent.selected','subagent.started','subagent.completed','subagent.failed','subagent.deselected'))
                      OR (SELECT COUNT(*) FROM session_native_ids native WHERE native.session_id=node.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY)<>1
                      OR receipt.carrier_digest IS NOT (
                        SELECT local_workspace_semantic_digest('session_sdk_subagent',native.native_session_id,run.native_run_id)
                        FROM local_workspace_node_source_references reference
                        JOIN session_events event ON event.event_id=reference.event_id
                        JOIN session_runs run ON run.session_id=event.session_id AND run.run_id=event.run_id
                        JOIN session_native_ids native ON native.session_id=event.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
                        WHERE reference.node_id=receipt.node_id LIMIT 1)
                      OR NOT EXISTS(SELECT 1 FROM local_workspace_subagent_lifecycle lifecycle WHERE lifecycle.node_id=receipt.node_id)
                      OR EXISTS(SELECT 1 FROM local_workspace_subagent_lifecycle lifecycle WHERE lifecycle.node_id=receipt.node_id AND (
                        lifecycle.selected_state IS NOT CASE WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.selected')>1 THEN 'inconsistent' WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.selected')=1 THEN 'recorded' ELSE 'not_observed' END
                        OR lifecycle.started_state IS NOT CASE WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.started')>1 THEN 'inconsistent' WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.started')=1 THEN 'recorded' ELSE 'not_observed' END
                        OR lifecycle.completed_state IS NOT CASE WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.completed')>1 OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.completed')>0 AND (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.failed')>0 THEN 'inconsistent' WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.completed')=1 THEN 'recorded' ELSE 'not_observed' END
                        OR lifecycle.failed_state IS NOT CASE WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.failed')>1 OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.failed')>0 AND (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.completed')>0 THEN 'inconsistent' WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.failed')=1 THEN 'recorded' ELSE 'not_observed' END
                        OR lifecycle.deselected_state IS NOT CASE WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.deselected')>1 THEN 'inconsistent' WHEN (SELECT COUNT(*) FROM local_workspace_node_source_references reference JOIN session_events event ON event.event_id=reference.event_id WHERE reference.node_id=receipt.node_id AND event.type='subagent.deselected')=1 THEN 'recorded' ELSE 'not_observed' END
                        OR lifecycle.input_state IS NOT 'source_unsupported'))))
                  OR receipt.source_family='otel' AND (
                    receipt.semantic_kind IS NOT 'tool' OR receipt.scope_kind IS NOT 'otel_span'
                    OR receipt.authority_receipt IS NOT 'otel-exact|normalized-tool-span|v1'
                    OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=receipt.node_id)<>1
                    OR EXISTS(
                      SELECT 1 FROM local_workspace_node_source_references reference
                      LEFT JOIN session_events event ON event.event_id=reference.event_id
                      LEFT JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id
                      WHERE reference.node_id=receipt.node_id AND (
                        reference.source_kind IS NOT 'otel_span'
                        OR reference.source_identity IS NOT reference.event_id
                        OR event.event_id IS NULL OR event.event_id IS NOT reference.event_id
                        OR event.session_id IS NOT node.session_id OR event.source_adapter IS NOT 'otel-exact'
                        OR event.run_id IS NULL OR execution.session_id IS NOT node.session_id
                        OR execution.source_kind IS NOT 'session_run' OR execution.source_identity IS NOT event.run_id
                        OR reference.trace_id IS NULL OR reference.span_id IS NULL
                        OR event.trace_id IS NOT reference.trace_id
                        OR event.source_event_id IS NOT reference.trace_id||'/'||reference.span_id
                        OR length(reference.trace_id)<>32 OR reference.trace_id<>lower(reference.trace_id) OR reference.trace_id GLOB '*[^0-9a-f]*'
                        OR length(reference.span_id)<>16 OR reference.span_id<>lower(reference.span_id) OR reference.span_id GLOB '*[^0-9a-f]*'
                        OR NOT EXISTS(SELECT 1 FROM monitor_spans span
                            WHERE span.trace_id=reference.trace_id COLLATE BINARY AND span.span_id=reference.span_id COLLATE BINARY
                              AND span.operation='execute_tool' COLLATE BINARY AND span.category IN ('tool_call','error'))
                        OR (SELECT COUNT(*) FROM session_events candidate WHERE candidate.session_id=event.session_id
                            AND candidate.source_adapter='otel-exact' COLLATE BINARY AND candidate.trace_id=reference.trace_id COLLATE BINARY
                            AND candidate.source_event_id=reference.trace_id||'/'||reference.span_id COLLATE BINARY)<>1
                        OR receipt.carrier_digest IS NOT local_workspace_semantic_digest('otel_tool',reference.trace_id,reference.span_id)
                        OR reference.revision_input IS NULL
                        OR reference.revision_input IS NOT event.source_adapter||'|'||event.source_event_id||'|'||(
                          SELECT CASE WHEN span.status IN ('ok','error') OR local_workspace_ticks(span.end_time) IS NOT NULL
                            THEN CASE WHEN span.status='error' THEN 'otel.tool.failed' ELSE 'otel.tool.completed' END||'|'||
                                 CASE WHEN local_workspace_ticks(span.start_time) IS NOT NULL THEN 'otel.tool.started' ELSE 'otel.tool.observed' END||'|2'
                            ELSE CASE WHEN local_workspace_ticks(span.start_time) IS NOT NULL THEN 'otel.tool.started|otel.tool.started|1' ELSE 'otel.tool.observed|otel.tool.observed|1' END END
                          FROM monitor_spans span WHERE span.trace_id=reference.trace_id COLLATE BINARY AND span.span_id=reference.span_id COLLATE BINARY
                            AND span.operation='execute_tool' COLLATE BINARY AND span.category IN ('tool_call','error') LIMIT 1)
                          ||'|'||event.occurred_at||'|'||receipt.authority_receipt)))
            );
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException();
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM local_workspace_skill_metadata metadata
              LEFT JOIN local_workspace_nodes node ON node.node_id=metadata.node_id
              LEFT JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id
              WHERE metadata.current_valid_state='certification_pending' AND (
                node.node_id IS NULL OR node.source_kind IS NOT 'skill_invocation' OR node.kind IS NOT 'skill'
                OR node.skill_activity_state IS NOT 'certification_pending' OR node.skill_activity_count IS NOT NULL
                OR execution.session_id IS NOT node.session_id OR execution.skill_activity_state IS NOT 'certification_pending'
                OR execution.skill_activity_count IS NOT NULL
                OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id)
                   <> (node.otel_source_identity IS NOT NULL)+(node.sdk_source_identity IS NOT NULL)
                OR (SELECT COUNT(*) FROM local_workspace_node_source_references reference WHERE reference.node_id=node.node_id) NOT BETWEEN 1 AND 2
                OR EXISTS(
                  SELECT 1 FROM local_workspace_node_source_references reference
                  LEFT JOIN session_events event ON event.event_id=reference.event_id
                  WHERE reference.node_id=node.node_id AND (
                    reference.source_kind IS NOT 'skill_claim'
                    OR reference.source_identity IS NOT node.otel_source_identity AND reference.source_identity IS NOT node.sdk_source_identity
                    OR reference.revision_input IS NOT node.source_identity||'|'||COALESCE(node.otel_source_identity,'')||'|'||COALESCE(node.sdk_source_identity,'')
                    OR event.event_id IS NULL OR event.session_id IS NOT node.session_id
                    OR execution.source_identity IS NOT event.run_id
                    OR reference.source_identity=node.otel_source_identity AND (
                      reference.trace_id IS NULL OR reference.span_id IS NULL
                      OR reference.trace_id IS NOT node.trace_id OR reference.span_id IS NOT node.span_id
                      OR event.source_adapter IS NOT 'otel-exact' OR event.trace_id IS NOT reference.trace_id
                      OR event.source_event_id IS NOT reference.trace_id||'/'||reference.span_id
                      OR NOT EXISTS(
                        SELECT 1 FROM skill_projection_invocations invocation
                        JOIN skill_projection_generations generation ON generation.generation_id=invocation.generation_id
                          AND generation.lifecycle='current'
                        JOIN skill_projection_trace_heads head ON head.trace_id=invocation.trace_id
                          AND head.current_generation_id=invocation.generation_id
                        JOIN source_trace_compatibility_revisions revision ON revision.trace_id=invocation.trace_id
                          AND revision.current_revision=generation.compatibility_revision
                          AND revision.current_effective_state='resolved'
                          AND revision.current_exact_version=invocation.source_application_version
                        WHERE invocation.source_arm='otel_trace_span'
                          AND invocation.session_id=node.session_id
                          AND 'otel:'||invocation.raw_record_id||':'||invocation.span_ordinal=reference.source_identity
                          AND invocation.trace_id=reference.trace_id
                          AND invocation.span_id=reference.span_id
                          AND invocation.skill_source IS metadata.source
                          AND invocation.invocation_trigger IS metadata.trigger
                          AND NOT EXISTS(SELECT 1 FROM skill_projection_generation_inputs input
                            WHERE input.generation_id=generation.generation_id
                              AND input.input_evidence_kind='deleted_before_digest_v10')))
                    OR reference.source_identity=node.sdk_source_identity AND (
                      reference.trace_id IS NOT NULL OR reference.span_id IS NOT NULL
                      OR event.source_adapter IS NOT 'copilot-sdk-stream' OR event.source_surface IS NOT 'copilot-sdk'))))
            );
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException();
    }

    private static void EnsureSkillOwnerCompileShapes(SqliteConnection connection, SqliteTransaction transaction)
    {
        var definitions = new (string Name, string Sql)[]
        {
            ("skill_projection_invocations", "CREATE TEMP TABLE skill_projection_invocations(generation_id INTEGER,source_arm TEXT,session_id TEXT,raw_record_id INTEGER,span_ordinal INTEGER,trace_id TEXT,span_id TEXT,skill_source TEXT,invocation_trigger TEXT,source_application_version TEXT);"),
            ("skill_projection_generations", "CREATE TEMP TABLE skill_projection_generations(generation_id INTEGER,lifecycle TEXT,compatibility_revision INTEGER);"),
            ("skill_projection_trace_heads", "CREATE TEMP TABLE skill_projection_trace_heads(trace_id TEXT,current_generation_id INTEGER);"),
            ("source_trace_compatibility_revisions", "CREATE TEMP TABLE source_trace_compatibility_revisions(trace_id TEXT,current_revision INTEGER,current_effective_state TEXT,current_exact_version TEXT);"),
            ("skill_projection_generation_inputs", "CREATE TEMP TABLE skill_projection_generation_inputs(generation_id INTEGER,input_evidence_kind TEXT);"),
        };
        foreach (var definition in definitions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);";
            command.Parameters.AddWithValue("$name", definition.Name);
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) continue;
            command.Parameters.Clear();
            command.CommandText = definition.Sql;
            command.ExecuteNonQuery();
        }
    }

    private static void ValidateSdkFactGraph(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset? publicationTime)
    {
        using var timeCommand = connection.CreateCommand();
        timeCommand.Transaction = transaction;
        timeCommand.CommandText = "SELECT refreshed_at FROM local_workspace_projection_state WHERE projector_key='local-workspace-projection-v1';";
        var timeText = publicationTime?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            ?? timeCommand.ExecuteScalar() as string;
        if (timeText is null || !DateTimeOffset.TryParseExact(timeText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            throw new InvalidOperationException();

        using var idsCommand = connection.CreateCommand();
        idsCommand.Transaction = transaction;
        idsCommand.CommandText = "SELECT session_id FROM local_workspace_sessions ORDER BY session_id COLLATE BINARY;";
        using var idsReader = idsCommand.ExecuteReader();
        var sessionIds = new List<string>();
        while (idsReader.Read()) sessionIds.Add(idsReader.GetString(0));
        idsReader.Close();

        var structuralSdkFacts = SkillProjectionReadService.ReadStructurallyValidSdkFactsForBackupValidation(
            connection, transaction, sessionIds, new StructuralValidationTimeProvider(instant));
        var expected = structuralSdkFacts
            .Select(static fact => (fact.SessionId, SourceIdentity: "sdk:" + fact.SourceIdentity,
                NormalizedText: fact.SkillName.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), fact.ExpiresAt))
            .ToHashSet();

        var expectedPending = structuralSdkFacts
            .Select(static fact => (fact.SessionId, SourceIdentity: "sdk:" + fact.SourceIdentity,
                fact.EventId, fact.ExecutionSourceIdentity, fact.SkillSource, fact.InvocationTrigger,
                fact.HistoricalSnapshotReference))
            .ToHashSet();

        using (var pendingCommand = connection.CreateCommand())
        {
            pendingCommand.Transaction = transaction;
            pendingCommand.CommandText = """
                SELECT node.session_id,reference.source_identity,reference.event_id,execution.source_identity,
                       metadata.source,metadata.trigger,metadata.historical_snapshot_reference
                FROM local_workspace_nodes node
                JOIN local_workspace_skill_metadata metadata ON metadata.node_id=node.node_id
                  AND metadata.current_valid_state='certification_pending'
                JOIN local_workspace_node_source_references reference ON reference.node_id=node.node_id
                  AND reference.source_identity=node.sdk_source_identity
                JOIN local_workspace_execution_headers execution ON execution.execution_id=node.execution_id
                WHERE node.source_kind='skill_invocation' AND node.sdk_source_identity IS NOT NULL
                ORDER BY node.session_id COLLATE BINARY,node.node_id COLLATE BINARY;
                """;
            using var pendingReader = pendingCommand.ExecuteReader();
            while (pendingReader.Read())
            {
                var actual = (
                    pendingReader.GetString(0), pendingReader.GetString(1),
                    pendingReader.IsDBNull(2) ? null : pendingReader.GetString(2),
                    pendingReader.IsDBNull(3) ? null : pendingReader.GetString(3),
                    pendingReader.IsDBNull(4) ? null : pendingReader.GetString(4),
                    pendingReader.IsDBNull(5) ? null : pendingReader.GetString(5),
                    pendingReader.IsDBNull(6) ? null : pendingReader.GetString(6));
                if (!expectedPending.Contains(actual)) throw new InvalidOperationException();
            }
        }

        using var factsCommand = connection.CreateCommand();
        factsCommand.Transaction = transaction;
        factsCommand.CommandText = "SELECT session_id,source_identity,normalized_text,expires_at FROM local_workspace_session_search_facts WHERE kind='skill' ORDER BY session_id,source_identity,normalized_text;";
        using var factsReader = factsCommand.ExecuteReader();
        while (factsReader.Read())
        {
            var sourceIdentity = factsReader.GetString(1);
            if (sourceIdentity.StartsWith("otel:", StringComparison.Ordinal)) continue;
            if (!sourceIdentity.StartsWith("sdk:", StringComparison.Ordinal)) throw new InvalidOperationException();
            var actual = (
                factsReader.GetString(0),
                sourceIdentity,
                factsReader.GetString(2),
                factsReader.IsDBNull(3) ? null : factsReader.GetString(3));
            if (!expected.Contains(actual)) throw new InvalidOperationException();
        }
    }

    private static void ValidateSpanFacts(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var availability = connection.CreateCommand())
        {
            availability.Transaction = transaction;
            availability.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='raw_records') AND EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='monitor_spans');";
            if (Convert.ToInt64(availability.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                availability.CommandText = "SELECT COUNT(*) FROM local_workspace_span_facts;";
                if (Convert.ToInt64(availability.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new InvalidOperationException();
                return;
            }
        }
        var monitorKeys = new HashSet<(long RawId, int Ordinal)>();
        using (var spans = connection.CreateCommand())
        {
            spans.Transaction = transaction;
            spans.CommandText = "SELECT raw_record_id,span_ordinal FROM monitor_spans ORDER BY raw_record_id,span_ordinal;";
            using var reader = spans.ExecuteReader();
            while (reader.Read()) monitorKeys.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        bool hasRetention;
        using (var retention = connection.CreateCommand())
        {
            retention.Transaction = transaction;
            retention.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='retention_items');";
            hasRetention = Convert.ToInt64(retention.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        var expected = new Dictionary<(long RawId, int Ordinal), (long? Retry, long? Total)>();
        var availableRawIds = new HashSet<long>();
        if (hasRetention)
        using (var records = connection.CreateCommand())
        {
            records.Transaction = transaction;
            records.CommandText = """
                SELECT r.id,r.source,r.trace_id,r.received_at,r.resource_attributes_json,r.payload_json,r.schema_version
                FROM raw_records r
                JOIN retention_items i ON i.store_kind='raw_record' AND i.source_item_id=CAST(r.id AS TEXT)
                WHERE i.state IN ('expiring','retained_by_policy') AND i.read_denied_at IS NULL
                  AND i.deleted_at IS NULL AND i.error_code IS NULL
                ORDER BY r.id;
                """;
            using var reader = records.ExecuteReader();
            while (reader.Read())
            {
                var rawId = reader.GetInt64(0);
                availableRawIds.Add(rawId);
                if (!DateTimeOffset.TryParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var receivedAt))
                    throw new InvalidOperationException();
                var raw = new RawTelemetryRecord(rawId, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), receivedAt,
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt32(6));
                foreach (var span in MonitorSpanProjectionBuilder.Build(raw))
                    if (monitorKeys.Contains((rawId, span.SpanOrdinal)))
                        expected.Add((rawId, span.SpanOrdinal), (span.RetryCount, span.ProducerTotalTokens));
            }
        }

        var actual = new Dictionary<(long RawId, int Ordinal), (long? Retry, long? Total)>();
        using (var facts = connection.CreateCommand())
        {
            facts.Transaction = transaction;
            facts.CommandText = "SELECT raw_record_id,span_ordinal,retry_count,producer_total_tokens FROM local_workspace_span_facts ORDER BY raw_record_id,span_ordinal;";
            using var reader = facts.ExecuteReader();
            while (reader.Read())
                actual.Add((reader.GetInt64(0), reader.GetInt32(1)),
                    (reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }
        if (hasRetention)
        using (var deleted = connection.CreateCommand())
        {
            deleted.Transaction = transaction;
            deleted.CommandText = """
                SELECT EXISTS(SELECT 1 FROM local_workspace_span_facts f
                  JOIN retention_items i ON i.store_kind='raw_record' AND i.source_item_id=CAST(f.raw_record_id AS TEXT)
                  JOIN retention_tombstones t ON t.item_id=i.item_id
                  WHERE i.state='deleted' AND i.read_denied_at IS NOT NULL AND i.deleted_at=t.deleted_at);
                """;
            if (Convert.ToInt64(deleted.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new InvalidOperationException();
        }
        if (actual.Keys.Any(key => !monitorKeys.Contains(key)))
            throw new InvalidOperationException();
        var availableActual = actual.Where(pair => availableRawIds.Contains(pair.Key.RawId)).ToDictionary();
        if (expected.Count != availableActual.Count || expected.Any(pair => !availableActual.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidOperationException();
    }

    private static void ValidateCanonicalProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset? publicationTime,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority,
        SqliteConnection? canonicalReplica)
    {
        var before = Snapshot(connection, transaction);
        if (publicationTime is null)
        {
            using var readNow = connection.CreateCommand();
            readNow.Transaction = transaction;
            readNow.CommandText = "SELECT refreshed_at FROM local_workspace_projection_state WHERE projector_key='local-workspace-projection-v1';";
            var text = readNow.ExecuteScalar() as string;
            if (text is null || !DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var projectorNow))
                throw new InvalidOperationException();
            publicationTime = projectorNow;
        }
        using var ownedReplica = canonicalReplica is null ? new SqliteConnection("Data Source=:memory:") : null;
        var replica = canonicalReplica ?? ownedReplica!;
        if (replica.State != System.Data.ConnectionState.Open)
        {
            replica.Open();
            connection.BackupDatabase(replica);
        }
        using (var replicaTransaction = replica.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(
                replica,
                replicaTransaction,
                publicationTime.Value,
                skillRegistryAuthority ?? FixedSkillRegistryGenerationAuthority.Load());
            if (!before.SequenceEqual(Snapshot(replica, replicaTransaction), StringComparer.Ordinal))
                throw new InvalidOperationException();
            replicaTransaction.Rollback();
        }
    }

    private static string[] Snapshot(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<string>();
        foreach (var table in LocalWorkspaceProjectionSchemaV1.TableNames.Order(StringComparer.Ordinal))
        {
            var columns = new List<string>();
            using (var info = connection.CreateCommand())
            {
                info.Transaction = transaction;
                info.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden=0 ORDER BY cid;";
                info.Parameters.AddWithValue("$table", table);
                using var reader = info.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(0));
            }
            var projection = string.Join(',', columns.Select(static column => $"\"{column.Replace("\"", "\"\"")}\""));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {projection} FROM \"{table}\" ORDER BY {projection};";
            using var data = command.ExecuteReader();
            while (data.Read())
            {
                var values = new string[data.FieldCount];
                for (var index = 0; index < values.Length; index++)
                    values[index] = data.IsDBNull(index) ? "N" : data.GetFieldType(index) == typeof(byte[]) ? "B" + Convert.ToHexString((byte[])data.GetValue(index)) : "V" + Convert.ToString(data.GetValue(index), CultureInfo.InvariantCulture);
                rows.Add(table + "\0" + string.Join("\0", values));
            }
        }
        return rows.ToArray();
    }

    private sealed class StructuralValidationTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

}

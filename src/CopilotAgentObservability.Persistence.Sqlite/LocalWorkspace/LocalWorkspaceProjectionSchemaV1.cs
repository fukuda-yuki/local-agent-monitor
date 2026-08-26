using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionSchemaV1
{
    internal const string ComponentName = "local_workspace_projection";
    internal const int Version = 3;
    internal static readonly string[] TableNames =
    [
        "local_workspace_sessions", "local_workspace_session_sources",
        "local_workspace_session_models", "local_workspace_session_search_facts", "local_workspace_session_activity",
        "local_workspace_token_observations", "local_workspace_span_facts", "local_workspace_projection_state",
    ];

    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> Definitions =
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
        [V2Sessions, .. Definitions.Where(static definition => definition.Name is not ("local_workspace_sessions" or "local_workspace_session_search_facts" or "local_workspace_search_facts_by_text" or "local_workspace_search_facts_by_session"))];
    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> V2ExpectedObjects = SqliteOwnedSchemaAuthority.Compile(V2Definitions);
    private static readonly string[] V2TableNames = V2Definitions.Where(static definition => definition.Type == "table").Select(static definition => definition.Name).ToArray();
    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> V1Definitions =
        V2Definitions.Where(static definition => definition.Name != "local_workspace_span_facts").ToArray();
    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> V1ExpectedObjects = SqliteOwnedSchemaAuthority.Compile(V1Definitions);
    internal static IEnumerable<SqliteOwnedSchemaObject> OwnedObjects => ExpectedObjects.Values;
    internal static IEnumerable<string> ExactV1SchemaSql => V1Definitions.Select(static definition => definition.Sql);
    internal static IEnumerable<string> ExactV2SchemaSql => V2Definitions.Select(static definition => definition.Sql);

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
            foreach (var definition in Definitions) Execute(connection, transaction, definition.Sql);
            BackfillSpanFacts(connection, transaction);
            RefreshProjection(connection, transaction, now, skillRegistryAuthority);
            Execute(connection, transaction, "UPDATE schema_version SET version=3 WHERE component='local_workspace_projection' AND version=2;");
            Validate(connection, transaction);
            return;
        }
        if (version is not null || owned.Count != 0)
        {
            Validate(connection, transaction);
            RefreshProjection(connection, transaction, now, skillRegistryAuthority);
            return;
        }
        foreach (var definition in Definitions) Execute(connection, transaction, definition.Sql);
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',3);");
        BackfillSpanFacts(connection, transaction);
        RefreshProjection(connection, transaction, now, skillRegistryAuthority);
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
            throw new InvalidOperationException("Unsupported incomplete local_workspace_projection schema version 3.");
    }

    internal static void ValidateCurrentOrExactLegacy(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = ReadVersion(connection, transaction);
        var owned = ReadOwnedObjects(connection, transaction);
        if (version == Version && SqliteOwnedSchemaAuthority.Equal(owned, ExpectedObjects)) return;
        if (version == 2 && SqliteOwnedSchemaAuthority.Equal(owned, V2ExpectedObjects)) return;
        if (version == 1 && SqliteOwnedSchemaAuthority.Equal(owned, V1ExpectedObjects)) return;
        throw new InvalidOperationException("Unsupported incomplete local_workspace_projection schema.");
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

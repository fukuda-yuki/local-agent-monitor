using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalRepositoryCatalogSchemaV1
{
    internal const string ComponentName = LocalRepositoryCatalogConstants.ComponentName;
    internal const int Version = LocalRepositoryCatalogConstants.Version;

    internal static readonly string[] TableNames =
    [
        "local_repositories", "local_repository_locators", "local_repository_locator_heads",
        "session_repository_observations", "session_repository_observation_contexts",
        "session_repository_manual_overrides", "session_repository_assignment_revisions",
        "session_repository_assignment_history", "local_repository_history",
        "local_repository_operation_receipts", "local_repository_reconciliation_state",
        "local_repository_reconciliation_queue",
    ];

    internal static readonly string[] IndexNames =
    [
        "IX_local_repository_locators_repository_created",
        "IX_session_repository_observation_contexts_session_observed",
        "IX_session_repository_observation_contexts_repository_session",
        "IX_session_repository_observations_raw_source",
        "IX_session_repository_manual_overrides_repository_session",
        "IX_local_repository_operation_receipts_created",
    ];

    internal static IReadOnlyList<(string Name, string Table, string Sql)> TriggerDefinitions { get; } =
        new[]
        {
            ("local_repository_locators", "locator_id=NEW.locator_id OR (kind=NEW.kind AND locator_sha256=NEW.locator_sha256) OR (repository_id=NEW.repository_id AND kind=NEW.kind AND locator_id=NEW.locator_id)"),
            ("session_repository_observations", "observation_id=NEW.observation_id OR source_identity_sha256=NEW.source_identity_sha256"),
            ("session_repository_observation_contexts", "context_id=NEW.context_id OR context_identity_sha256=NEW.context_identity_sha256 OR (observation_id=NEW.observation_id AND session_event_id=NEW.session_event_id)"),
            ("session_repository_assignment_history", "history_id=NEW.history_id OR (session_id=NEW.session_id AND new_revision=NEW.new_revision)"),
            ("local_repository_history", "history_id=NEW.history_id OR (repository_id=NEW.repository_id AND new_revision=NEW.new_revision)"),
            ("local_repository_operation_receipts", "operation_key=NEW.operation_key"),
        }
        .SelectMany(static item => new[]
        {
            ($"{item.Item1}_update_rejected", item.Item1, $"CREATE TRIGGER {item.Item1}_update_rejected BEFORE UPDATE ON {item.Item1} BEGIN SELECT RAISE(ABORT,'local_repository_catalog_append_only'); END;"),
            ($"{item.Item1}_delete_rejected", item.Item1, $"CREATE TRIGGER {item.Item1}_delete_rejected BEFORE DELETE ON {item.Item1} BEGIN SELECT RAISE(ABORT,'local_repository_catalog_append_only'); END;"),
            ($"{item.Item1}_insert_replacement_rejected", item.Item1, $"CREATE TRIGGER {item.Item1}_insert_replacement_rejected BEFORE INSERT ON {item.Item1} WHEN EXISTS(SELECT 1 FROM {item.Item1} WHERE {item.Item2}) BEGIN SELECT RAISE(ABORT,'local_repository_catalog_append_only'); END;"),
        })
        .ToArray();

    internal static void Ensure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var declared = ReadDeclaredVersion(connection, transaction);
        var objects = ReadOwnedObjects(connection, transaction);
        if (declared is not null || objects.Count != 0)
        {
            if (declared == Version && SqliteOwnedSchemaAuthority.Equal(objects, LegacyExpectedObjects))
                MigrateLegacyCatalog(connection, transaction);
            Validate(connection, transaction);
            return;
        }

        ValidateDependencies(connection, transaction);
        foreach (var definition in InstallDefinitions)
            Execute(connection, transaction, definition.Sql);
        foreach (var trigger in TriggerDefinitions)
            Execute(connection, transaction, trigger.Sql.Replace("CREATE TRIGGER ", "CREATE TRIGGER IF NOT EXISTS ", StringComparison.Ordinal));
        Execute(connection, transaction, """
            INSERT INTO local_repository_reconciliation_state(
                projector_key,
                last_discovered_span_id,
                updated_at)
            VALUES(
                'local-repository-catalog-v1',
                NULL,
                '1970-01-01T00:00:00.0000000+00:00');
            """);
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('local_repository_catalog',1);");
    }

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (ReadDeclaredVersion(connection, transaction) != Version)
            Reject();
        ValidateDependencies(connection, transaction);
        if (!HasExactOwnedSchema(connection, transaction))
            Reject();
        LocalRepositoryCatalogValidation.ValidateRows(connection, transaction);
    }

    internal static bool HasExactOwnedSchema(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Equal(ReadOwnedObjects(connection, transaction), ExpectedObjects);

    internal static bool HasExactSupportedBackupSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var objects = ReadOwnedObjects(connection, transaction);
        return SqliteOwnedSchemaAuthority.Equal(objects, ExpectedObjects)
            || SqliteOwnedSchemaAuthority.Equal(objects, LegacyExpectedObjects);
    }

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ReadOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(connection, transaction, static (name, table) =>
            name.Equals("local_repositories", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("local_repository_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("session_repository_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("IX_local_repository_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("IX_session_repository_", StringComparison.OrdinalIgnoreCase)
            || table.Equals("local_repositories", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("local_repository_", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("session_repository_", StringComparison.OrdinalIgnoreCase));

    private static long? ReadDeclaredVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "schema_version"))
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version,typeof(version) FROM schema_version WHERE component='local_repository_catalog';";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetString(1) != "integer")
            Reject();
        var version = reader.GetInt64(0);
        if (reader.Read())
            Reject();
        return version;
    }

    private static void ValidateDependencies(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "schema_version")
            || !ObjectExists(connection, transaction, "table", "sessions")
            || !ObjectExists(connection, transaction, "table", "session_events"))
        {
            throw new InvalidOperationException("local_repository_catalog_component_dependency_invalid");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version,typeof(version) FROM schema_version WHERE component='session';";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(1) != "integer" || reader.GetInt64(0) != 14 || reader.Read())
            throw new InvalidOperationException("local_repository_catalog_component_dependency_invalid");
    }

    private static bool ObjectExists(SqliteConnection connection, SqliteTransaction? transaction, string type, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type=$type AND name=$name);";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Reject() => throw new InvalidOperationException("Unsupported incomplete local_repository_catalog schema version 1.");

    private static string Uuid(string column) => $"typeof({column})='text' AND length({column})=36 AND {column} GLOB '????????-????-7???-[89ab]???-????????????' AND {column} NOT GLOB '*[^0-9a-f-]*' AND length(replace({column},'-',''))=32";
    private static string Hex(string column, int length) => $"typeof({column})='text' AND length({column})={length} AND {column} NOT GLOB '*[^0-9a-f]*'";
    private static string Timestamp(string column) => $"""
        typeof({column})='text' AND length({column})=33 AND {column} GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
        AND substr({column},1,4) BETWEEN '0001' AND '9999' AND substr({column},6,2) BETWEEN '01' AND '12' AND substr({column},12,2) BETWEEN '00' AND '23' AND substr({column},15,2) BETWEEN '00' AND '59' AND substr({column},18,2) BETWEEN '00' AND '59'
        AND CAST(substr({column},9,2) AS INTEGER) BETWEEN 1 AND CASE CAST(substr({column},6,2) AS INTEGER)
          WHEN 2 THEN CASE WHEN CAST(substr({column},1,4) AS INTEGER)%4=0 AND (CAST(substr({column},1,4) AS INTEGER)%100<>0 OR CAST(substr({column},1,4) AS INTEGER)%400=0) THEN 29 ELSE 28 END
          WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30 ELSE 31 END
        """;
    private static string OperationKey(string column) => $"typeof({column})='text' AND length({column})=48 AND substr({column},1,5)='lrc1_' AND substr({column},6) NOT GLOB '*[^A-Za-z0-9_-]*' AND substr({column},48,1) IN ('A','E','I','M','Q','U','Y','c','g','k','o','s','w','0','4','8')";

    private static readonly string SchemaSql = $$"""
        CREATE TABLE local_repositories(
          repository_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("repository_id")}}),
          display_name TEXT COLLATE BINARY NOT NULL CHECK(typeof(display_name)='text'),
          revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=1),
          created_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("created_at")}}), updated_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("updated_at")}}));
        CREATE TABLE local_repository_locators(
          locator_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("locator_id")}}), repository_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("repository_id")}}),
          kind TEXT COLLATE BINARY NOT NULL CHECK(typeof(kind)='text' AND kind IN ('github_repository','local_git_repository')), canonical_locator TEXT COLLATE BINARY NOT NULL CHECK(typeof(canonical_locator)='text'),
          locator_sha256 TEXT COLLATE BINARY NOT NULL CHECK({{Hex("locator_sha256", 64)}}), source TEXT COLLATE BINARY NOT NULL CHECK(typeof(source)='text' AND source IN ('observed','manual') AND (kind='github_repository' OR source='observed')),
          display_owner TEXT COLLATE BINARY NOT NULL CHECK(typeof(display_owner)='text'), display_repository TEXT COLLATE BINARY NOT NULL CHECK(typeof(display_repository)='text'), created_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("created_at")}}),
          UNIQUE(kind,locator_sha256), UNIQUE(repository_id,kind,locator_id), UNIQUE(repository_id,locator_id),
          FOREIGN KEY(repository_id) REFERENCES local_repositories(repository_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE local_repository_locator_heads(
          repository_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("repository_id")}}), kind TEXT COLLATE BINARY NOT NULL CHECK(typeof(kind)='text' AND kind IN ('github_repository','local_git_repository')), locator_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("locator_id")}}), updated_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("updated_at")}}),
          PRIMARY KEY(repository_id,kind), FOREIGN KEY(repository_id) REFERENCES local_repositories(repository_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
          FOREIGN KEY(repository_id,kind,locator_id) REFERENCES local_repository_locators(repository_id,kind,locator_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE session_repository_observations(
          observation_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("observation_id")}}),
          source_identity_sha256 TEXT COLLATE BINARY NOT NULL UNIQUE CHECK({{Hex("source_identity_sha256", 64)}}), raw_record_id INTEGER NOT NULL CHECK(typeof(raw_record_id)='integer' AND raw_record_id>0), raw_payload_sha256 TEXT COLLATE BINARY NOT NULL CHECK({{Hex("raw_payload_sha256", 64)}}),
          resource_span_ordinal INTEGER NOT NULL CHECK(typeof(resource_span_ordinal)='integer' AND resource_span_ordinal BETWEEN 0 AND 2147483647), scope_span_ordinal INTEGER NULL CHECK(scope_span_ordinal IS NULL OR (typeof(scope_span_ordinal)='integer' AND scope_span_ordinal BETWEEN 0 AND 2147483647)), span_ordinal INTEGER NULL CHECK(span_ordinal IS NULL OR (typeof(span_ordinal)='integer' AND span_ordinal BETWEEN 0 AND 2147483647)), attribute_ordinal INTEGER NOT NULL CHECK(typeof(attribute_ordinal)='integer' AND attribute_ordinal BETWEEN 0 AND 2147483647),
          scope_kind TEXT COLLATE BINARY NOT NULL CHECK(typeof(scope_kind)='text' AND scope_kind IN ('resource','span')), attribute_key TEXT COLLATE BINARY NOT NULL CHECK(typeof(attribute_key)='text' AND attribute_key IN ('vcs.repository.url.full','copilot_chat.repo.remote_url','native_workspace_git_remote','native_workspace_git_common_dir')), value_classification TEXT COLLATE BINARY NOT NULL CHECK(typeof(value_classification)='text' AND value_classification IN ('admitted','invalid_locator','invalid_type','duplicate_key')),
          locator_kind TEXT COLLATE BINARY NULL CHECK(locator_kind IS NULL OR (typeof(locator_kind)='text' AND locator_kind IN ('github_repository','local_git_repository'))), canonical_locator TEXT COLLATE BINARY NULL CHECK(canonical_locator IS NULL OR typeof(canonical_locator)='text'), locator_sha256 TEXT COLLATE BINARY NULL CHECK(locator_sha256 IS NULL OR ({{Hex("locator_sha256", 64)}})), display_owner TEXT COLLATE BINARY NULL CHECK(display_owner IS NULL OR typeof(display_owner)='text'), display_repository TEXT COLLATE BINARY NULL CHECK(display_repository IS NULL OR typeof(display_repository)='text'),
          source_surface TEXT COLLATE BINARY NOT NULL CHECK(typeof(source_surface)='text' AND source_surface IN ('github-copilot-cli','github-copilot-vscode')), source_application_version TEXT COLLATE BINARY NULL CHECK(source_application_version IS NULL OR typeof(source_application_version)='text'), observed_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("observed_at")}}),
          CHECK((scope_kind='resource' AND scope_span_ordinal IS NULL AND span_ordinal IS NULL) OR (scope_kind='span' AND scope_span_ordinal IS NOT NULL AND span_ordinal IS NOT NULL)),
          CHECK((value_classification='admitted' AND locator_kind IS NOT NULL AND canonical_locator IS NOT NULL AND locator_sha256 IS NOT NULL AND display_owner IS NOT NULL AND display_repository IS NOT NULL) OR (value_classification<>'admitted' AND locator_kind IS NULL AND canonical_locator IS NULL AND locator_sha256 IS NULL AND display_owner IS NULL AND display_repository IS NULL)));
        CREATE TABLE session_repository_observation_contexts(
          context_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("context_id")}}), observation_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("observation_id")}}), context_identity_sha256 TEXT COLLATE BINARY NOT NULL UNIQUE CHECK({{Hex("context_identity_sha256", 64)}}),
          session_event_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("session_event_id")}}), session_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("session_id")}}), trace_id TEXT COLLATE BINARY NOT NULL CHECK({{Hex("trace_id", 32)}}), span_id TEXT COLLATE BINARY NOT NULL CHECK({{Hex("span_id", 16)}}), admission_state TEXT COLLATE BINARY NOT NULL CHECK(typeof(admission_state)='text' AND admission_state IN ('admitted','shadowed','invalid_locator','invalid_type','duplicate_key')),
          repository_id TEXT COLLATE BINARY NULL CHECK(repository_id IS NULL OR {{Uuid("repository_id")}}), locator_id TEXT COLLATE BINARY NULL CHECK(locator_id IS NULL OR {{Uuid("locator_id")}}), observed_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("observed_at")}}),
          UNIQUE(observation_id,session_event_id), CHECK((admission_state='admitted' AND repository_id IS NOT NULL AND locator_id IS NOT NULL) OR (admission_state<>'admitted' AND repository_id IS NULL AND locator_id IS NULL)),
          FOREIGN KEY(observation_id) REFERENCES session_repository_observations(observation_id) ON UPDATE RESTRICT ON DELETE RESTRICT, FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE RESTRICT, FOREIGN KEY(session_id,session_event_id) REFERENCES session_events(session_id,event_id) ON UPDATE RESTRICT ON DELETE RESTRICT, FOREIGN KEY(repository_id,locator_id) REFERENCES local_repository_locators(repository_id,locator_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE session_repository_manual_overrides(session_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("session_id")}}), state TEXT COLLATE BINARY NOT NULL CHECK(typeof(state)='text' AND state IN ('assigned','explicitly_unassigned')), repository_id TEXT COLLATE BINARY NULL CHECK(repository_id IS NULL OR {{Uuid("repository_id")}}), revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=1), updated_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("updated_at")}}), CHECK((state='assigned' AND repository_id IS NOT NULL) OR (state='explicitly_unassigned' AND repository_id IS NULL)), FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE RESTRICT, FOREIGN KEY(repository_id) REFERENCES local_repositories(repository_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE session_repository_assignment_revisions(session_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("session_id")}}), revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0), updated_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("updated_at")}}), FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE session_repository_assignment_history(
          history_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("history_id")}}), session_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("session_id")}}), action TEXT COLLATE BINARY NOT NULL CHECK(typeof(action)='text' AND action IN ('assign','explicitly_unassign','resume_automatic','automatic_reconcile')), previous_revision INTEGER NOT NULL CHECK(typeof(previous_revision)='integer' AND previous_revision>=0), new_revision INTEGER NOT NULL CHECK(typeof(new_revision)='integer' AND new_revision=previous_revision+1), previous_assignment_state_sha256 TEXT COLLATE BINARY NOT NULL CHECK({{Hex("previous_assignment_state_sha256", 64)}}), new_assignment_state_sha256 TEXT COLLATE BINARY NOT NULL CHECK({{Hex("new_assignment_state_sha256", 64)}}),
          previous_state TEXT COLLATE BINARY NOT NULL CHECK(typeof(previous_state)='text' AND previous_state IN ('assigned','unassigned','explicitly_unassigned','conflict')), new_state TEXT COLLATE BINARY NOT NULL CHECK(typeof(new_state)='text' AND new_state IN ('assigned','unassigned','explicitly_unassigned','conflict')), previous_authority TEXT COLLATE BINARY NOT NULL CHECK(typeof(previous_authority)='text' AND previous_authority IN ('automatic','manual','none')), new_authority TEXT COLLATE BINARY NOT NULL CHECK(typeof(new_authority)='text' AND new_authority IN ('automatic','manual','none')), previous_repository_id TEXT COLLATE BINARY NULL CHECK(previous_repository_id IS NULL OR {{Uuid("previous_repository_id")}}), new_repository_id TEXT COLLATE BINARY NULL CHECK(new_repository_id IS NULL OR {{Uuid("new_repository_id")}}),
          cause_kind TEXT COLLATE BINARY NOT NULL CHECK(typeof(cause_kind)='text' AND cause_kind IN ('user_operation','source_reconciliation')), operation_key TEXT COLLATE BINARY NULL CHECK(operation_key IS NULL OR {{OperationKey("operation_key")}}), reconciliation_fingerprint TEXT COLLATE BINARY NULL CHECK(reconciliation_fingerprint IS NULL OR {{Hex("reconciliation_fingerprint", 64)}}), occurred_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("occurred_at")}}), UNIQUE(session_id,new_revision),
          CHECK((cause_kind='user_operation' AND operation_key IS NOT NULL AND reconciliation_fingerprint IS NULL AND action IN ('assign','explicitly_unassign','resume_automatic')) OR (cause_kind='source_reconciliation' AND operation_key IS NULL AND reconciliation_fingerprint IS NOT NULL AND action='automatic_reconcile')),
          CHECK((previous_state='assigned' AND previous_authority IN ('automatic','manual') AND previous_repository_id IS NOT NULL) OR (previous_state='unassigned' AND previous_authority='none' AND previous_repository_id IS NULL) OR (previous_state='explicitly_unassigned' AND previous_authority='manual' AND previous_repository_id IS NULL) OR (previous_state='conflict' AND previous_authority='automatic' AND previous_repository_id IS NULL)),
          CHECK((new_state='assigned' AND new_authority IN ('automatic','manual') AND new_repository_id IS NOT NULL) OR (new_state='unassigned' AND new_authority='none' AND new_repository_id IS NULL) OR (new_state='explicitly_unassigned' AND new_authority='manual' AND new_repository_id IS NULL) OR (new_state='conflict' AND new_authority='automatic' AND new_repository_id IS NULL)),
          FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE RESTRICT, FOREIGN KEY(previous_repository_id) REFERENCES local_repositories(repository_id) ON UPDATE RESTRICT ON DELETE RESTRICT, FOREIGN KEY(new_repository_id) REFERENCES local_repositories(repository_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE local_repository_history(history_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("history_id")}}), repository_id TEXT COLLATE BINARY NOT NULL CHECK({{Uuid("repository_id")}}), action TEXT COLLATE BINARY NOT NULL CHECK(typeof(action)='text' AND action IN ('create','create_observed','rename','add_locator','replace_locator')), previous_revision INTEGER NOT NULL CHECK(typeof(previous_revision)='integer' AND previous_revision>=0), new_revision INTEGER NOT NULL CHECK(typeof(new_revision)='integer'), locator_id TEXT COLLATE BINARY NULL CHECK(locator_id IS NULL OR {{Uuid("locator_id")}}), cause_kind TEXT COLLATE BINARY NOT NULL CHECK(typeof(cause_kind)='text' AND cause_kind IN ('user_operation','source_context')), operation_key TEXT COLLATE BINARY NULL CHECK(operation_key IS NULL OR {{OperationKey("operation_key")}}), context_identity_sha256 TEXT COLLATE BINARY NULL CHECK(context_identity_sha256 IS NULL OR {{Hex("context_identity_sha256", 64)}}), occurred_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("occurred_at")}}), UNIQUE(repository_id,new_revision), CHECK((action IN ('create','create_observed') AND previous_revision=0 AND new_revision=1) OR (action NOT IN ('create','create_observed') AND previous_revision>=1 AND new_revision=previous_revision+1)), CHECK((cause_kind='user_operation' AND operation_key IS NOT NULL AND context_identity_sha256 IS NULL AND action<>'create_observed') OR (cause_kind='source_context' AND operation_key IS NULL AND context_identity_sha256 IS NOT NULL AND action='create_observed')), CHECK((action IN ('create_observed','add_locator','replace_locator') AND locator_id IS NOT NULL) OR (action='rename' AND locator_id IS NULL) OR action='create'), FOREIGN KEY(repository_id) REFERENCES local_repositories(repository_id) ON UPDATE RESTRICT ON DELETE RESTRICT, FOREIGN KEY(locator_id) REFERENCES local_repository_locators(locator_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE local_repository_operation_receipts(operation_key TEXT COLLATE BINARY PRIMARY KEY CHECK({{OperationKey("operation_key")}}), request_fingerprint TEXT COLLATE BINARY NOT NULL CHECK({{Hex("request_fingerprint", 64)}}), status_code INTEGER NOT NULL CHECK(typeof(status_code)='integer' AND status_code IN (200,201)), content_type TEXT COLLATE BINARY NOT NULL CHECK(typeof(content_type)='text' AND content_type='application/json; charset=utf-8'), cache_control TEXT COLLATE BINARY NOT NULL CHECK(typeof(cache_control)='text' AND cache_control='no-store'), response_entity BLOB NOT NULL CHECK(typeof(response_entity)='blob'), created_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("created_at")}}));
        CREATE TABLE local_repository_reconciliation_state(projector_key TEXT COLLATE BINARY PRIMARY KEY CHECK(typeof(projector_key)='text' AND projector_key='local-repository-catalog-v1'), last_discovered_span_id INTEGER NULL CHECK(last_discovered_span_id IS NULL OR (typeof(last_discovered_span_id)='integer' AND last_discovered_span_id>0)), updated_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("updated_at")}}));
        CREATE TABLE local_repository_reconciliation_queue(queue_id TEXT COLLATE BINARY PRIMARY KEY CHECK({{Uuid("queue_id")}}), raw_record_id INTEGER NOT NULL CHECK(typeof(raw_record_id)='integer' AND raw_record_id>0), input_evidence_kind TEXT COLLATE BINARY NOT NULL CHECK(typeof(input_evidence_kind)='text' AND input_evidence_kind IN ('payload_sha256','input_unavailable')), raw_payload_sha256 TEXT COLLATE BINARY NULL CHECK(raw_payload_sha256 IS NULL OR {{Hex("raw_payload_sha256", 64)}}), projector_version TEXT COLLATE BINARY NOT NULL CHECK(typeof(projector_version)='text' AND projector_version='local-repository-catalog:1'), reconciliation_fingerprint TEXT COLLATE BINARY NOT NULL CHECK({{Hex("reconciliation_fingerprint", 64)}}), state TEXT COLLATE BINARY NOT NULL CHECK(typeof(state)='text' AND state IN ('pending','waiting_session','leased','completed','input_unavailable','failed_terminal')), attempt_count INTEGER NOT NULL CHECK(typeof(attempt_count)='integer' AND attempt_count>=0), lease_token TEXT COLLATE BINARY NULL CHECK(lease_token IS NULL OR {{Hex("lease_token", 64)}}), lease_expires_at TEXT COLLATE BINARY NULL CHECK(lease_expires_at IS NULL OR {{Timestamp("lease_expires_at")}}), terminal_reason TEXT COLLATE BINARY NULL CHECK(terminal_reason IS NULL OR (typeof(terminal_reason)='text' AND terminal_reason IN ('catalog_identity_conflict','catalog_session_identity_conflict','catalog_cardinality_exceeded','catalog_payload_digest_mismatch','catalog_parse_failure','catalog_schema_violation'))), created_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("created_at")}}), updated_at TEXT COLLATE BINARY NOT NULL CHECK({{Timestamp("updated_at")}}), UNIQUE(raw_record_id,projector_version), CHECK((input_evidence_kind='payload_sha256' AND raw_payload_sha256 IS NOT NULL) OR (input_evidence_kind='input_unavailable' AND raw_payload_sha256 IS NULL)), CHECK((state='leased' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL) OR (state<>'leased' AND lease_token IS NULL AND lease_expires_at IS NULL)), CHECK((state='failed_terminal' AND terminal_reason IS NOT NULL) OR (state<>'failed_terminal' AND terminal_reason IS NULL)));
        CREATE INDEX IX_local_repository_locators_repository_created ON local_repository_locators(repository_id,created_at,locator_id);
        CREATE INDEX IX_session_repository_observation_contexts_session_observed ON session_repository_observation_contexts(session_id,observed_at,observation_id);
        CREATE INDEX IX_session_repository_observation_contexts_repository_session ON session_repository_observation_contexts(repository_id,session_id);
        CREATE INDEX IX_session_repository_observations_raw_source ON session_repository_observations(raw_record_id,source_identity_sha256);
        CREATE INDEX IX_session_repository_manual_overrides_repository_session ON session_repository_manual_overrides(repository_id,session_id);
        CREATE INDEX IX_local_repository_operation_receipts_created ON local_repository_operation_receipts(created_at,operation_key);
        """;

    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> InstallDefinitions =
        BuildInstallDefinitions(SchemaSql);

    private static readonly string LegacySchemaSql = SchemaSql
        .Replace("kind IN ('github_repository','local_git_repository')", "kind='github_repository'", StringComparison.Ordinal)
        .Replace(" AND (kind='github_repository' OR source='observed')", string.Empty, StringComparison.Ordinal)
        .Replace("attribute_key IN ('vcs.repository.url.full','copilot_chat.repo.remote_url','native_workspace_git_remote','native_workspace_git_common_dir')", "attribute_key IN ('vcs.repository.url.full','copilot_chat.repo.remote_url')", StringComparison.Ordinal)
        .Replace("locator_kind IN ('github_repository','local_git_repository')", "locator_kind='github_repository'", StringComparison.Ordinal);

    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> LegacyExpectedObjects =
        SqliteOwnedSchemaAuthority.Compile(
            BuildInstallDefinitions(LegacySchemaSql).Concat(TriggerDefinitions.Select(static trigger =>
                new SqliteOwnedSchemaDefinition("trigger", trigger.Name, trigger.Table, trigger.Sql))).ToArray());

    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ExpectedObjects =
        SqliteOwnedSchemaAuthority.Compile(
            InstallDefinitions.Concat(TriggerDefinitions.Select(static trigger =>
                new SqliteOwnedSchemaDefinition("trigger", trigger.Name, trigger.Table, trigger.Sql))).ToArray());

    private static IReadOnlyList<SqliteOwnedSchemaDefinition> BuildInstallDefinitions(string sql)
    {
        var statements = SplitStatements(sql);
        if (statements.Count != TableNames.Length + IndexNames.Length)
            throw new InvalidOperationException("local_repository_catalog_schema_definitions_invalid");
        var definitions = new List<SqliteOwnedSchemaDefinition>(statements.Count);
        for (var index = 0; index < TableNames.Length; index++)
            definitions.Add(new SqliteOwnedSchemaDefinition("table", TableNames[index], TableNames[index], statements[index]));

        var indexTables = new[]
        {
            "local_repository_locators",
            "session_repository_observation_contexts",
            "session_repository_observation_contexts",
            "session_repository_observations",
            "session_repository_manual_overrides",
            "local_repository_operation_receipts",
        };
        for (var index = 0; index < IndexNames.Length; index++)
        {
            definitions.Add(new SqliteOwnedSchemaDefinition(
                "index",
                IndexNames[index],
                indexTables[index],
                statements[TableNames.Length + index]));
        }
        return definitions.AsReadOnly();
    }

    private static void MigrateLegacyCatalog(SqliteConnection connection, SqliteTransaction transaction)
    {
        var tables = new[]
        {
            "local_repository_locators", "local_repository_locator_heads",
            "session_repository_observations", "session_repository_observation_contexts",
            "local_repository_history",
        };
        foreach (var trigger in TriggerDefinitions.Where(trigger => tables.Contains(trigger.Table, StringComparer.Ordinal)))
            Execute(connection, transaction, $"DROP TRIGGER {trigger.Name};");
        foreach (var definition in BuildInstallDefinitions(LegacySchemaSql).Where(definition => definition.Type == "index" && tables.Contains(definition.Table, StringComparer.Ordinal)))
            Execute(connection, transaction, $"DROP INDEX {definition.Name};");
        foreach (var table in tables.Reverse())
            Execute(connection, transaction, $"ALTER TABLE {table} RENAME TO {table}_legacy_native_locator;");
        foreach (var table in new[]
        {
            "local_repository_locators", "session_repository_observations",
            "local_repository_locator_heads", "session_repository_observation_contexts",
            "local_repository_history",
        })
            Execute(connection, transaction, InstallDefinitions.Single(definition => definition.Type == "table" && definition.Name == table).Sql);
        foreach (var table in new[]
        {
            "local_repository_locators", "session_repository_observations",
            "local_repository_locator_heads", "session_repository_observation_contexts",
            "local_repository_history",
        })
            Execute(connection, transaction, $"INSERT INTO {table} SELECT * FROM {table}_legacy_native_locator;");
        foreach (var table in new[]
        {
            "local_repository_history", "session_repository_observation_contexts",
            "local_repository_locator_heads", "session_repository_observations",
            "local_repository_locators",
        })
            Execute(connection, transaction, $"DROP TABLE {table}_legacy_native_locator;");
        foreach (var definition in InstallDefinitions.Where(definition => definition.Type == "index" && tables.Contains(definition.Table, StringComparer.Ordinal)))
            Execute(connection, transaction, definition.Sql);
        foreach (var trigger in TriggerDefinitions.Where(trigger => tables.Contains(trigger.Table, StringComparer.Ordinal)))
            Execute(connection, transaction, trigger.Sql);
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.Transaction = transaction;
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read()) throw new InvalidOperationException("local_repository_catalog_legacy_migration_failed");
        }
        Execute(connection, transaction, """
            UPDATE local_repository_reconciliation_queue
            SET state='pending',updated_at=strftime('%Y-%m-%dT%H:%M:%f0000+00:00','now')
            WHERE state='completed'
              AND NOT EXISTS(SELECT 1 FROM session_repository_observations observation WHERE observation.raw_record_id=local_repository_reconciliation_queue.raw_record_id);
            """);
    }

    private static IReadOnlyList<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var start = 0;
        char? quoted = null;
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quoted is { } closing)
            {
                if (character != closing)
                    continue;
                if (index + 1 < sql.Length && sql[index + 1] == closing)
                {
                    index++;
                    continue;
                }
                quoted = null;
                continue;
            }
            if (character is '\'' or '"' or '`')
            {
                quoted = character;
                continue;
            }
            if (character != ';')
                continue;
            statements.Add(sql[start..(index + 1)]);
            start = index + 1;
        }
        if (!string.IsNullOrWhiteSpace(sql[start..]))
            throw new InvalidOperationException("local_repository_catalog_schema_definitions_invalid");
        return statements;
    }
}

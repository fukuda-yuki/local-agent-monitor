using System.Data;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal static class LocalRepositoryCatalogBackupValidation
{
    private const int PageSize = 128;
    private const string InvalidState = "local_repository_catalog_backup_invalid";

    internal static void Validate(SqliteConnection connection, SqliteTransaction transaction) =>
        ValidateCore(connection, transaction, observer: null);

    internal static void Validate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryCatalogBackupValidationObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ValidateCore(connection, transaction, observer);
    }

    private static void ValidateCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryCatalogBackupValidationObserver? observer)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            Reject();

        observer?.PhaseEntered(LocalRepositoryCatalogBackupValidationPhase.Structure);
        ValidateStructure(connection, transaction);
        observer?.PhaseEntered(LocalRepositoryCatalogBackupValidationPhase.Guards);
        ValidateStorage(connection, transaction);
        observer?.PhaseEntered(LocalRepositoryCatalogBackupValidationPhase.RawReferences);
        ValidateRawReferences(connection, transaction, observer);
        observer?.PhaseEntered(LocalRepositoryCatalogBackupValidationPhase.Reconciliation);
        var reconciliationState = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction);
        observer?.PhaseEntered(LocalRepositoryCatalogBackupValidationPhase.AutomaticAdmission);
        SqliteLocalRepositoryCatalogStore.ValidateRestorableAutomaticAdmissionState(
            connection,
            transaction,
            reconciliationState);
        observer?.PhaseEntered(LocalRepositoryCatalogBackupValidationPhase.Mutation);
        SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, transaction);
    }

    private static void ValidateStructure(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!HasExactComponentVersion(connection, transaction, "session", 13)
            || !HasExactComponentVersion(connection, transaction, LocalRepositoryCatalogSchemaV1.ComponentName, LocalRepositoryCatalogSchemaV1.Version)
            || !SqliteSessionStore.IsCurrentSchemaValid(connection, transaction)
            || !LocalRepositoryCatalogSchemaV1.HasExactOwnedSchema(connection, transaction))
        {
            Reject();
        }
    }

    private static bool HasExactComponentVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string component,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN typeof(version)='integer' AND version=$version THEN 1 ELSE 0 END),0)
            FROM schema_version
            WHERE component=$component COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$component", component);
        command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) == 1 && reader.GetInt64(1) == 1 && !reader.Read();
    }

    private static void ValidateStorage(SqliteConnection connection, SqliteTransaction transaction)
    {
        var guards = new (string Table, string Invalid)[]
        {
            ("local_repositories", Join(
                InvalidText("repository_id", 36, 36),
                InvalidText("display_name", 1, 800),
                InvalidInteger("revision", "revision>=1"),
                InvalidText("created_at", 33, 33),
                InvalidText("updated_at", 33, 33))),
            ("local_repository_locators", Join(
                InvalidText("locator_id", 36, 36),
                InvalidText("repository_id", 36, 36),
                InvalidLiteral("kind", "'github_repository'"),
                InvalidAscii("canonical_locator", 1, 151),
                InvalidText("locator_sha256", 64, 64),
                InvalidLiteral("source", "'observed','manual'"),
                InvalidAscii("display_owner", 1, 39),
                InvalidAscii("display_repository", 1, 100),
                InvalidText("created_at", 33, 33))),
            ("local_repository_locator_heads", Join(
                InvalidText("repository_id", 36, 36),
                InvalidLiteral("kind", "'github_repository'"),
                InvalidText("locator_id", 36, 36),
                InvalidText("updated_at", 33, 33))),
            ("session_repository_observations", Join(
                InvalidText("observation_id", 36, 36),
                InvalidText("source_identity_sha256", 64, 64),
                InvalidInteger("raw_record_id", "raw_record_id>0"),
                InvalidText("raw_payload_sha256", 64, 64),
                InvalidInteger("resource_span_ordinal", "resource_span_ordinal BETWEEN 0 AND 2147483647"),
                InvalidNullableInteger("scope_span_ordinal", "scope_span_ordinal BETWEEN 0 AND 2147483647"),
                InvalidNullableInteger("span_ordinal", "span_ordinal BETWEEN 0 AND 2147483647"),
                InvalidInteger("attribute_ordinal", "attribute_ordinal BETWEEN 0 AND 2147483647"),
                InvalidLiteral("scope_kind", "'resource','span'"),
                InvalidLiteral("attribute_key", "'vcs.repository.url.full','copilot_chat.repo.remote_url'"),
                InvalidLiteral("value_classification", "'admitted','invalid_locator','invalid_type','duplicate_key'"),
                InvalidNullableLiteral("locator_kind", "'github_repository'"),
                InvalidNullableAscii("canonical_locator", 1, 151),
                InvalidNullableText("locator_sha256", 64, 64),
                InvalidNullableAscii("display_owner", 1, 39),
                InvalidNullableAscii("display_repository", 1, 100),
                InvalidLiteral("source_surface", "'github-copilot-cli','github-copilot-vscode'"),
                InvalidNullableText("source_application_version", 1, 64),
                InvalidText("observed_at", 33, 33))),
            ("session_repository_observation_contexts", Join(
                InvalidText("context_id", 36, 36),
                InvalidText("observation_id", 36, 36),
                InvalidText("context_identity_sha256", 64, 64),
                InvalidText("session_event_id", 36, 36),
                InvalidText("session_id", 36, 36),
                InvalidText("trace_id", 32, 32),
                InvalidText("span_id", 16, 16),
                InvalidLiteral("admission_state", "'admitted','shadowed','invalid_locator','invalid_type','duplicate_key'"),
                InvalidNullableText("repository_id", 36, 36),
                InvalidNullableText("locator_id", 36, 36),
                InvalidText("observed_at", 33, 33))),
            ("session_repository_manual_overrides", Join(
                InvalidText("session_id", 36, 36),
                InvalidLiteral("state", "'assigned','explicitly_unassigned'"),
                InvalidNullableText("repository_id", 36, 36),
                InvalidInteger("revision", "revision>=1"),
                InvalidText("updated_at", 33, 33))),
            ("session_repository_assignment_revisions", Join(
                InvalidText("session_id", 36, 36),
                InvalidInteger("revision", "revision>=0"),
                InvalidText("updated_at", 33, 33))),
            ("session_repository_assignment_history", Join(
                InvalidText("history_id", 36, 36),
                InvalidText("session_id", 36, 36),
                InvalidLiteral("action", "'assign','explicitly_unassign','resume_automatic','automatic_reconcile'"),
                InvalidInteger("previous_revision", "previous_revision>=0"),
                InvalidInteger("new_revision", "new_revision>=1"),
                "new_revision<>previous_revision+1",
                InvalidText("previous_assignment_state_sha256", 64, 64),
                InvalidText("new_assignment_state_sha256", 64, 64),
                InvalidLiteral("previous_state", "'assigned','unassigned','explicitly_unassigned','conflict'"),
                InvalidLiteral("new_state", "'assigned','unassigned','explicitly_unassigned','conflict'"),
                InvalidLiteral("previous_authority", "'automatic','manual','none'"),
                InvalidLiteral("new_authority", "'automatic','manual','none'"),
                InvalidNullableText("previous_repository_id", 36, 36),
                InvalidNullableText("new_repository_id", 36, 36),
                InvalidLiteral("cause_kind", "'user_operation','source_reconciliation'"),
                InvalidNullableText("operation_key", 48, 48),
                InvalidNullableText("reconciliation_fingerprint", 64, 64),
                InvalidText("occurred_at", 33, 33))),
            ("local_repository_history", Join(
                InvalidText("history_id", 36, 36),
                InvalidText("repository_id", 36, 36),
                InvalidLiteral("action", "'create','create_observed','rename','add_locator','replace_locator'"),
                InvalidInteger("previous_revision", "previous_revision>=0"),
                InvalidInteger("new_revision", "new_revision>=1"),
                InvalidNullableText("locator_id", 36, 36),
                InvalidLiteral("cause_kind", "'user_operation','source_context'"),
                InvalidNullableText("operation_key", 48, 48),
                InvalidNullableText("context_identity_sha256", 64, 64),
                InvalidText("occurred_at", 33, 33))),
            ("local_repository_operation_receipts", Join(
                InvalidText("operation_key", 48, 48),
                InvalidText("request_fingerprint", 64, 64),
                InvalidInteger("status_code", "status_code IN (200,201)"),
                InvalidLiteral("content_type", "'application/json; charset=utf-8'"),
                InvalidLiteral("cache_control", "'no-store'"),
                $"typeof(response_entity)<>'blob' OR length(response_entity) NOT BETWEEN 1 AND {LocalRepositoryExactResponse.MaximumEntityBytes}",
                InvalidText("created_at", 33, 33))),
            ("local_repository_reconciliation_state", Join(
                InvalidLiteral("projector_key", "'local-repository-catalog-v1'"),
                InvalidNullableInteger("last_discovered_span_id", "last_discovered_span_id>0"),
                InvalidText("updated_at", 33, 33))),
            ("local_repository_reconciliation_queue", Join(
                InvalidText("queue_id", 36, 36),
                InvalidInteger("raw_record_id", "raw_record_id>0"),
                InvalidLiteral("input_evidence_kind", "'payload_sha256','input_unavailable'"),
                InvalidNullableText("raw_payload_sha256", 64, 64),
                InvalidLiteral("projector_version", "'local-repository-catalog:1'"),
                InvalidText("reconciliation_fingerprint", 64, 64),
                InvalidLiteral("state", "'pending','waiting_session','leased','completed','input_unavailable','failed_terminal'"),
                InvalidInteger("attempt_count", "attempt_count>=0"),
                InvalidNullableText("lease_token", 64, 64),
                InvalidNullableText("lease_expires_at", 33, 33),
                InvalidNullableLiteral("terminal_reason", "'catalog_identity_conflict','catalog_session_identity_conflict','catalog_cardinality_exceeded','catalog_payload_digest_mismatch','catalog_parse_failure','catalog_schema_violation'"),
                InvalidText("created_at", 33, 33),
                InvalidText("updated_at", 33, 33))),
        };

        foreach (var (table, invalid) in guards)
            RejectIfInvalid(connection, transaction, table, invalid);

        RejectIfInvalid(connection, transaction, "session_repository_observations",
            "NOT EXISTS(SELECT 1 FROM source_schema_observations s WHERE s.raw_record_id=session_repository_observations.raw_record_id)");

        RejectIfInvalid(connection, transaction, "source_schema_observations", Join(
            InvalidInteger("raw_record_id", "raw_record_id>0"),
            InvalidLiteral("input_evidence_kind", "'payload_sha256'"),
            InvalidText("raw_payload_sha256", 64, 64),
            InvalidLiteral("source_surface", "'github-copilot-cli','github-copilot-vscode'"),
            InvalidNullableText("source_application_version", 1, 64),
            InvalidText("observed_at", 33, 33)),
            "EXISTS(SELECT 1 FROM session_repository_observations o WHERE o.raw_record_id=source_schema_observations.raw_record_id)");

        RejectIfInvalid(connection, transaction, "session_events", Join(
            InvalidText("event_id", 36, 36),
            InvalidText("session_id", 36, 36),
            InvalidLiteral("type", "'otel.span'"),
            InvalidText("trace_id", 32, 32),
            InvalidLiteral("source_surface", "'copilot-cli','vscode'"),
            InvalidLiteral("source_adapter", "'otel-exact'"),
            InvalidText("source_event_id", 49, 49)),
            """
            EXISTS(
                SELECT 1 FROM session_repository_observation_contexts c
                WHERE (c.session_event_id=session_events.event_id AND c.session_id=session_events.session_id)
                   OR (session_events.source_adapter='otel-exact' COLLATE BINARY
                       AND session_events.source_event_id=c.trace_id || '/' || c.span_id))
            """);

        RejectIfInvalid(connection, transaction, "raw_records",
            $"typeof(payload_json)<>'text' OR length(CAST(payload_json AS BLOB)) NOT BETWEEN 1 AND {RawReplayLimits.MaximumRawRecordBytes}",
            """
            EXISTS(SELECT 1 FROM local_repository_reconciliation_queue q WHERE q.raw_record_id=raw_records.id)
            OR EXISTS(SELECT 1 FROM session_repository_observations o WHERE o.raw_record_id=raw_records.id)
            """);
    }

    private static void ValidateRawReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryCatalogBackupValidationObserver? observer)
    {
        var afterRawRecordId = 0L;
        while (true)
        {
            var page = new List<long>(PageSize);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT raw_record_id
                    FROM (
                        SELECT raw_record_id FROM local_repository_reconciliation_queue
                        UNION
                        SELECT raw_record_id FROM session_repository_observations)
                    WHERE raw_record_id>$after
                    ORDER BY raw_record_id
                    LIMIT 128;
                    """;
                command.Parameters.AddWithValue("$after", afterRawRecordId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    page.Add(reader.GetInt64(0));
            }
            if (page.Count == 0)
                return;
            observer?.RawIdPageMaterialized(page.Count);
            foreach (var rawRecordId in page)
                ValidateRawReference(connection, transaction, rawRecordId, observer);
            afterRawRecordId = page[^1];
        }
    }

    private static void ValidateRawReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        ILocalRepositoryCatalogBackupValidationObserver? observer)
    {
        string? expectedDigest = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT digest
                FROM (
                    SELECT raw_payload_sha256 AS digest
                    FROM local_repository_reconciliation_queue
                    WHERE raw_record_id=$raw AND input_evidence_kind='payload_sha256'
                    UNION
                    SELECT raw_payload_sha256 AS digest
                    FROM session_repository_observations
                    WHERE raw_record_id=$raw)
                ORDER BY digest COLLATE BINARY
                LIMIT 2;
                """;
            command.Parameters.AddWithValue("$raw", rawRecordId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
                expectedDigest = reader.GetString(0);
            if (reader.Read())
                Reject();
        }
        if (expectedDigest is null)
            return;

        using var payloadCommand = connection.CreateCommand();
        payloadCommand.Transaction = transaction;
        payloadCommand.CommandText = "SELECT payload_json FROM raw_records WHERE id=$raw LIMIT 2;";
        payloadCommand.Parameters.AddWithValue("$raw", rawRecordId);
        using var payloadReader = payloadCommand.ExecuteReader();
        if (!payloadReader.Read())
            return;
        var payload = payloadReader.GetString(0);
        observer?.RawPayloadMaterialized(1);
        if (payloadReader.Read())
            Reject();
        var actualDigest = SkillProjectionHashing.InputDigest(payload);
        observer?.RawPayloadHashed();
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
            Reject();
    }

    private static void RejectIfInvalid(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string invalid,
        string? reachable = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM \"{table}\" WHERE {(reachable is null ? string.Empty : $"({reachable}) AND ")}({invalid}) LIMIT 1);";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            Reject();
    }

    private static string InvalidText(string column, int minimumBytes, int maximumBytes) =>
        $"typeof({column})<>'text' OR length(CAST({column} AS BLOB)) NOT BETWEEN {minimumBytes} AND {maximumBytes}";

    private static string InvalidNullableText(string column, int minimumBytes, int maximumBytes) =>
        $"typeof({column}) NOT IN ('null','text') OR typeof({column})='text' AND length(CAST({column} AS BLOB)) NOT BETWEEN {minimumBytes} AND {maximumBytes}";

    private static string InvalidAscii(string column, int minimumBytes, int maximumBytes) =>
        $"{InvalidText(column, minimumBytes, maximumBytes)} OR {column} GLOB '*[^ -~]*'";

    private static string InvalidNullableAscii(string column, int minimumBytes, int maximumBytes) =>
        $"{InvalidNullableText(column, minimumBytes, maximumBytes)} OR typeof({column})='text' AND {column} GLOB '*[^ -~]*'";

    private static string InvalidLiteral(string column, string accepted) =>
        $"typeof({column})<>'text' OR {column} NOT IN ({accepted})";

    private static string InvalidNullableLiteral(string column, string accepted) =>
        $"typeof({column}) NOT IN ('null','text') OR typeof({column})='text' AND {column} NOT IN ({accepted})";

    private static string InvalidInteger(string column, string accepted) =>
        $"typeof({column})<>'integer' OR NOT ({accepted})";

    private static string InvalidNullableInteger(string column, string accepted) =>
        $"typeof({column}) NOT IN ('null','integer') OR typeof({column})='integer' AND NOT ({accepted})";

    private static string Join(params string[] values) => string.Join(" OR ", values.Select(static value => $"({value})"));

    private static void Reject() => throw new InvalidOperationException(InvalidState);
}

internal enum LocalRepositoryCatalogBackupValidationPhase
{
    Structure,
    Guards,
    RawReferences,
    Reconciliation,
    AutomaticAdmission,
    Mutation,
}

internal interface ILocalRepositoryCatalogBackupValidationObserver
{
    void PhaseEntered(LocalRepositoryCatalogBackupValidationPhase phase);
    void RawIdPageMaterialized(int count);
    void RawPayloadMaterialized(int count);
    void RawPayloadHashed();
}

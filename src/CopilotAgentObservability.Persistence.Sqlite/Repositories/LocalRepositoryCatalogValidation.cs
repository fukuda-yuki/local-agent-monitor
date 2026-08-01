using System.Globalization;
using System.Text;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalRepositoryCatalogValidation
{
    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction) =>
        LocalRepositoryCatalogSchemaV1.Validate(connection, transaction);

    internal static bool IsCanonicalUuidV7(string? value) =>
        value is { Length: 36 }
        && value[8] == '-' && value[13] == '-' && value[18] == '-' && value[23] == '-'
        && value[14] == '7' && value[19] is '8' or '9' or 'a' or 'b'
        && value.Where((_, index) => index is not (8 or 13 or 18 or 23)).All(IsLowerHex)
        && Guid.TryParseExact(value, "D", out var parsed)
        && parsed.Version == 7
        && parsed.ToString("D") == value;

    internal static bool IsCanonicalTimestamp(string? value) =>
        value is { Length: 33 }
        && value.EndsWith("+00:00", StringComparison.Ordinal)
        && DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        && parsed.Offset == TimeSpan.Zero
        && parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) == value;

    internal static bool IsLowerSha256(string? value) => value is { Length: 64 } && value.All(IsLowerHex);

    internal static void ValidateRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ValidateForeignKeys(connection, transaction);
        ValidateRepositoryRows(connection, transaction);
        ValidateLocatorRows(connection, transaction);
        ValidateObservationRows(connection, transaction);
        ValidateContextRows(connection, transaction);
        ValidateMutableRows(connection, transaction);
        ValidateHistoryRows(connection, transaction);
        ValidateReceiptRows(connection, transaction);
        ValidateQueueRows(connection, transaction);
    }

    private static void ValidateRepositoryRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT repository_id,display_name,created_at,updated_at FROM local_repositories;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!IsCanonicalUuidV7(reader.GetString(0)) || !IsDisplayName(reader.GetString(1))
                || !IsCanonicalTimestamp(reader.GetString(2)) || !IsCanonicalTimestamp(reader.GetString(3)))
                Reject();
        }
    }

    private static void ValidateLocatorRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT locator_id,repository_id,canonical_locator,locator_sha256,display_owner,display_repository,created_at FROM local_repository_locators;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!IsCanonicalUuidV7(reader.GetString(0)) || !IsCanonicalUuidV7(reader.GetString(1))
                || !HasExactLocatorFields(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))
                || !IsCanonicalTimestamp(reader.GetString(6)))
                Reject();
        }
    }

    private static void ValidateObservationRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,scope_kind,attribute_key,source_application_version,observed_at,value_classification,locator_kind,canonical_locator,locator_sha256,display_owner,display_repository FROM session_repository_observations;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sourceIdentity = SourceIdentity(
                reader.GetInt64(2), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(8), reader.GetInt32(7), reader.GetString(9));
            if (!IsCanonicalUuidV7(reader.GetString(0)) || !IsLowerSha256(reader.GetString(1)) || reader.GetString(1) != sourceIdentity || !IsLowerSha256(reader.GetString(3))
                || !IsApprovedObservationAttributeKey(reader.GetString(9))
                || (!reader.IsDBNull(10) && !IsVisibleVersion(reader.GetString(10))) || !IsCanonicalTimestamp(reader.GetString(11))
                || (reader.GetString(12) == "admitted" && (!"github_repository".Equals(reader.GetString(13), StringComparison.Ordinal)
                    || !HasExactLocatorFields(reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetString(17)))))
                Reject();
        }
    }

    private static bool HasExactLocatorFields(string canonicalLocator, string locatorSha256, string displayOwner, string displayRepository) =>
        GitHubRepositoryLocatorParser.IsExact(canonicalLocator, locatorSha256, displayOwner, displayRepository);

    private static void ValidateContextRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, """
            SELECT c.context_id,c.context_identity_sha256,c.session_id,c.session_event_id,c.trace_id,c.span_id,c.observed_at,c.observation_id,
                   c.admission_state,c.repository_id,c.locator_id,o.source_identity_sha256,o.value_classification,o.scope_kind
            FROM session_repository_observation_contexts c
            JOIN session_repository_observations o ON o.observation_id=c.observation_id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!IsCanonicalUuidV7(reader.GetString(0)) || !IsLowerSha256(reader.GetString(1)) || reader.GetString(1) != ContextIdentity(reader.GetString(11), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))
                || !IsCanonicalUuidV7(reader.GetString(2)) || !IsCanonicalUuidV7(reader.GetString(3))
                || !IsLowerHex(reader.GetString(4), 32) || !IsLowerHex(reader.GetString(5), 16)
                || !IsCanonicalTimestamp(reader.GetString(6))
                || !HasPhysicalAdmissionMapping(reader.GetString(8), reader.IsDBNull(9), reader.IsDBNull(10), reader.GetString(12), reader.GetString(13)))
                Reject();
        }
    }

    private static bool HasPhysicalAdmissionMapping(string state, bool repositoryIsNull, bool locatorIsNull, string classification, string scopeKind) =>
        state == "shadowed"
            ? scopeKind == "resource" && repositoryIsNull && locatorIsNull
            : state == classification
                && (state == "admitted" ? !repositoryIsNull && !locatorIsNull : repositoryIsNull && locatorIsNull);

    private static void ValidateHistoryRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (Exists(connection, transaction, """
            SELECT 1 FROM local_repository_history
            WHERE (cause_kind='source_context' AND (action<>'create_observed' OR context_identity_sha256 IS NULL OR operation_key IS NOT NULL))
               OR (cause_kind='user_operation' AND (action='create_observed' OR operation_key IS NULL OR context_identity_sha256 IS NOT NULL))
            UNION ALL
            SELECT 1 FROM session_repository_assignment_history
            WHERE (cause_kind='source_reconciliation' AND (action<>'automatic_reconcile' OR reconciliation_fingerprint IS NULL OR operation_key IS NOT NULL))
               OR (cause_kind='user_operation' AND (action='automatic_reconcile' OR operation_key IS NULL OR reconciliation_fingerprint IS NOT NULL))
            LIMIT 1;
            """))
            Reject();
        using var histories = Command(connection, transaction, """
            SELECT history_id,session_id,previous_assignment_state_sha256,new_assignment_state_sha256,previous_repository_id,new_repository_id,operation_key,reconciliation_fingerprint,occurred_at FROM session_repository_assignment_history
            UNION ALL SELECT history_id,repository_id,NULL,NULL,NULL,NULL,operation_key,context_identity_sha256,occurred_at FROM local_repository_history;
            """);
        using var reader = histories.ExecuteReader();
        while (reader.Read())
        {
            if (!IsCanonicalUuidV7(reader.GetString(0)) || !IsCanonicalUuidV7(reader.GetString(1))
                || (!reader.IsDBNull(2) && !IsLowerSha256(reader.GetString(2))) || (!reader.IsDBNull(3) && !IsLowerSha256(reader.GetString(3)))
                || (!reader.IsDBNull(4) && !IsCanonicalUuidV7(reader.GetString(4))) || (!reader.IsDBNull(5) && !IsCanonicalUuidV7(reader.GetString(5)))
                || (!reader.IsDBNull(6) && !IsOperationKey(reader.GetString(6))) || (!reader.IsDBNull(7) && !IsLowerSha256(reader.GetString(7)))
                || !IsCanonicalTimestamp(reader.GetString(8)))
                Reject();
        }
        if (Exists(connection, transaction, """
            SELECT 1 FROM local_repository_history h
            WHERE (h.locator_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM local_repository_locators l WHERE l.locator_id=h.locator_id AND l.repository_id=h.repository_id))
               OR (h.cause_kind='source_context' AND NOT EXISTS(SELECT 1 FROM session_repository_observation_contexts c WHERE c.context_identity_sha256=h.context_identity_sha256))
               OR (h.action='create_observed' AND NOT EXISTS(SELECT 1 FROM session_repository_observation_contexts c WHERE c.context_identity_sha256=h.context_identity_sha256 AND c.admission_state='admitted' AND c.repository_id=h.repository_id AND c.locator_id=h.locator_id))
               OR (h.cause_kind='user_operation' AND NOT EXISTS(SELECT 1 FROM local_repository_operation_receipts r WHERE r.operation_key=h.operation_key))
            UNION ALL
            SELECT 1 FROM session_repository_assignment_history h
            WHERE (h.cause_kind='source_reconciliation' AND NOT EXISTS(SELECT 1 FROM local_repository_reconciliation_queue q WHERE q.reconciliation_fingerprint=h.reconciliation_fingerprint))
               OR (h.cause_kind='user_operation' AND NOT EXISTS(SELECT 1 FROM local_repository_operation_receipts r WHERE r.operation_key=h.operation_key))
            LIMIT 1;
            """))
            Reject();
    }

    private static void ValidateReceiptRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT operation_key,request_fingerprint,created_at FROM local_repository_operation_receipts;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!IsOperationKey(reader.GetString(0)) || !IsLowerSha256(reader.GetString(1)) || !IsCanonicalTimestamp(reader.GetString(2)))
                Reject();
        }
    }

    private static void ValidateMutableRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (Exists(connection, transaction, """
            SELECT 1 FROM local_repository_locator_heads WHERE NOT EXISTS(SELECT 1 FROM local_repositories r WHERE r.repository_id=local_repository_locator_heads.repository_id) OR NOT EXISTS(SELECT 1 FROM local_repository_locators l WHERE l.locator_id=local_repository_locator_heads.locator_id AND l.repository_id=local_repository_locator_heads.repository_id AND l.kind=local_repository_locator_heads.kind)
            UNION ALL SELECT 1 FROM session_repository_manual_overrides WHERE NOT EXISTS(SELECT 1 FROM sessions s WHERE s.session_id=session_repository_manual_overrides.session_id)
            UNION ALL SELECT 1 FROM session_repository_assignment_revisions WHERE NOT EXISTS(SELECT 1 FROM sessions s WHERE s.session_id=session_repository_assignment_revisions.session_id)
            LIMIT 1;
            """))
            Reject();
        ValidateCanonicalTimestampColumn(connection, transaction, "local_repository_locator_heads", "updated_at");
        ValidateCanonicalTimestampColumn(connection, transaction, "session_repository_manual_overrides", "updated_at");
        ValidateCanonicalTimestampColumn(connection, transaction, "session_repository_assignment_revisions", "updated_at");
        ValidateCanonicalTimestampColumn(connection, transaction, "local_repository_reconciliation_state", "updated_at");
    }

    private static void ValidateCanonicalTimestampColumn(SqliteConnection connection, SqliteTransaction? transaction, string table, string column)
    {
        using var command = Command(connection, transaction, $"SELECT {column} FROM {table};");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!IsCanonicalTimestamp(reader.GetString(0)))
                Reject();
    }

    private static void ValidateQueueRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,reconciliation_fingerprint,lease_token,lease_expires_at,created_at,updated_at FROM local_repository_reconciliation_queue;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var digest = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (!IsCanonicalUuidV7(reader.GetString(0)) || !IsLowerSha256(reader.GetString(5))
                || reader.GetString(5) != ReconciliationFingerprint(reader.GetInt64(1), reader.GetString(2), digest, reader.GetString(4))
                || (!reader.IsDBNull(6) && !IsLowerSha256(reader.GetString(6)))
                || (!reader.IsDBNull(7) && !IsCanonicalTimestamp(reader.GetString(7)))
                || !IsCanonicalTimestamp(reader.GetString(8)) || !IsCanonicalTimestamp(reader.GetString(9)))
                Reject();
        }
    }

    private static void ValidateForeignKeys(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "PRAGMA foreign_key_check;");
        using var reader = command.ExecuteReader();
        if (reader.Read())
            Reject();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar() is not null;
    }

    private static string ScalarText(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal static bool IsDisplayName(string value)
    {
        if (value.Length == 0 || HasUnpairedSurrogate(value) || value != value.Normalize(NormalizationForm.FormC)
            || System.Text.Encoding.UTF8.GetByteCount(value) > 800 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return false;
        var scalarCount = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index]))
                    return false;
            }
            else if (char.IsLowSurrogate(character))
                return false;
            scalarCount++;
            if (char.IsControl(character) || (character >= '\u007f' && character <= '\u009f') || (character >= '\u202a' && character <= '\u202e') || (character >= '\u2066' && character <= '\u2069'))
                return false;
        }
        return scalarCount is >= 1 and <= 200;
    }

    private static bool IsVisibleVersion(string value) => value.Length is >= 1 and <= 64 && value.All(character => character is >= '!' and <= '~' && character is not '/' and not '\\');
    private static bool IsApprovedObservationAttributeKey(string value) => value is "vcs.repository.url.full" or "copilot_chat.repo.remote_url";
    private static bool HasUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index]))
                    return true;
            }
            else if (char.IsLowSurrogate(value[index]))
                return true;
        }
        return false;
    }
    internal static bool IsOperationKey(string value)
    {
        if (value.Length != 48 || !value.StartsWith("lrc1_", StringComparison.Ordinal)
            || !value[5..].All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            return false;
        try
        {
            var payload = value[5..].Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(payload + "=");
            return bytes.Length == 32
                && "lrc1_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }
    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static bool IsLowerHex(string value, int length) => value.Length == length && value.All(IsLowerHex);

    private static string SourceIdentity(long rawRecordId, int resourceOrdinal, int? scopeOrdinal, int? spanOrdinal, string scopeKind, int attributeOrdinal, string attributeKey)
    {
        try
        {
            var input = scopeKind switch
            {
                "resource" when scopeOrdinal is null && spanOrdinal is null =>
                    LocalRepositorySourceIdentityInput.Resource(rawRecordId, resourceOrdinal, attributeOrdinal, attributeKey),
                "span" when scopeOrdinal is not null && spanOrdinal is not null =>
                    LocalRepositorySourceIdentityInput.Span(rawRecordId, resourceOrdinal, scopeOrdinal.Value, spanOrdinal.Value, attributeOrdinal, attributeKey),
                _ => throw new ArgumentException("Invalid persisted observation scope."),
            };
            return LocalRepositoryIdentityHashing.SourceIdentity(input);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (OverflowException)
        {
            return string.Empty;
        }
    }

    private static string ContextIdentity(string sourceIdentity, string sessionId, string eventId, string traceId, string spanId)
    {
        try
        {
            return LocalRepositoryIdentityHashing.ContextIdentity(new LocalRepositoryContextIdentityInput(
                sourceIdentity, sessionId, eventId, traceId, spanId));
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string ReconciliationFingerprint(long rawRecordId, string evidenceKind, string? digest, string projectorVersion)
    {
        if (projectorVersion != LocalRepositoryCatalogConstants.ProjectorVersion)
            return string.Empty;
        try
        {
            var evidence = evidenceKind switch
            {
                "payload_sha256" when digest is not null => LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, digest),
                "input_unavailable" when digest is null => LocalRepositoryReconciliationEvidence.InputUnavailable(rawRecordId),
                _ => throw new ArgumentException("Invalid persisted reconciliation evidence."),
            };
            return LocalRepositoryIdentityHashing.ReconciliationFingerprint(evidence);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
    private static void Reject() => throw new InvalidOperationException("local_repository_catalog_canonical_value_invalid");
}

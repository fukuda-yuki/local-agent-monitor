using System.Data;
using System.Globalization;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteLocalRepositoryCatalogStore
{
    private const int AutomaticAdmissionValidationPageSize = 128;
    private const string AutomaticAdmissionStateInvalid = "local_repository_automatic_admission_state_invalid";
    private const string ReconciliationStateTransactionMismatch = "local_repository_reconciliation_state_transaction_mismatch";

    internal static void ValidateRestorableAutomaticAdmissionState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryValidatedReconciliationState reconciliationState)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(reconciliationState);
        if (!reconciliationState.IsBoundTo(connection, transaction)
            || connection.State != ConnectionState.Open
            || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException(ReconciliationStateTransactionMismatch);
        }

        ValidateAutomaticAdmissionObservations(connection, transaction);
        ValidateAutomaticAdmissionContexts(connection, transaction);
        ValidateObservedLocatorCreation(connection, transaction);
        ValidateAutomaticAdmissionHistory(connection, transaction, reconciliationState);
    }

    private static void ValidateAutomaticAdmissionObservations(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var afterRawRecordId = 0L;
        var afterSourceIdentity = string.Empty;
        LocalRepositoryCaptureProvenance? currentProvenance = null;
        while (true)
        {
            var page = new List<AutomaticAdmissionObservationRow>(AutomaticAdmissionValidationPageSize);
            using (var command = AdmissionCommand(connection, transaction, """
                SELECT o.observation_id,o.source_identity_sha256,o.raw_record_id,o.raw_payload_sha256,
                       o.resource_span_ordinal,o.scope_span_ordinal,o.span_ordinal,o.attribute_ordinal,
                       o.scope_kind,o.attribute_key,o.value_classification,o.locator_kind,
                       o.canonical_locator,o.locator_sha256,o.display_owner,o.display_repository,
                       o.source_surface,o.source_application_version,o.observed_at,
                       EXISTS(SELECT 1 FROM session_repository_observation_contexts c
                              WHERE c.observation_id=o.observation_id LIMIT 1)
                FROM session_repository_observations o
                WHERE o.raw_record_id>$after_raw
                   OR (o.raw_record_id=$after_raw
                       AND o.source_identity_sha256>$after_source COLLATE BINARY)
                ORDER BY o.raw_record_id,o.source_identity_sha256 COLLATE BINARY
                LIMIT 128;
                """))
            {
                command.Parameters.AddWithValue("$after_raw", afterRawRecordId);
                command.Parameters.AddWithValue("$after_source", afterSourceIdentity);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    page.Add(ReadObservation(reader));
            }

            if (page.Count == 0)
                return;
            foreach (var row in page)
            {
                if (!IsValidAutomaticAdmissionObservation(row))
                    RejectAutomaticAdmission();
                if (currentProvenance is null || currentProvenance.RawRecordId != row.RawRecordId)
                {
                    var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
                        connection,
                        transaction,
                        row.RawRecordId,
                        row.RawPayloadSha256);
                    if (result.Status != LocalRepositoryCaptureProvenanceStatus.Valid
                        || result.Provenance is null)
                    {
                        RejectAutomaticAdmission();
                    }
                    currentProvenance = result.Provenance!;
                }
                if (currentProvenance.RawPayloadSha256 != row.RawPayloadSha256
                    || currentProvenance.SourceSurface != row.SourceSurface
                    || currentProvenance.SourceApplicationVersion != row.SourceApplicationVersion
                    || currentProvenance.ObservedAt.ToString("O", CultureInfo.InvariantCulture) != row.ObservedAt)
                {
                    RejectAutomaticAdmission();
                }
            }

            var last = page[^1];
            afterRawRecordId = last.RawRecordId;
            afterSourceIdentity = last.SourceIdentitySha256;
        }
    }

    private static AutomaticAdmissionObservationRow ReadObservation(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetString(10),
        NullableText(reader, 11),
        NullableText(reader, 12),
        NullableText(reader, 13),
        NullableText(reader, 14),
        NullableText(reader, 15),
        reader.GetString(16),
        NullableText(reader, 17),
        reader.GetString(18),
        reader.GetInt64(19) == 1);

    private static bool IsValidAutomaticAdmissionObservation(AutomaticAdmissionObservationRow row)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.ObservationId)
            || !LocalRepositoryCatalogValidation.IsLowerSha256(row.SourceIdentitySha256)
            || row.RawRecordId < 1
            || !LocalRepositoryCatalogValidation.IsLowerSha256(row.RawPayloadSha256)
            || row.ResourceSpanOrdinal < 0
            || row.AttributeOrdinal < 0
            || row.AttributeKey is not ("vcs.repository.url.full" or "copilot_chat.repo.remote_url")
            || row.SourceSurface is not ("github-copilot-cli" or "github-copilot-vscode")
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(row.ObservedAt)
            || !row.HasContext)
        {
            return false;
        }

        LocalRepositorySourceIdentityInput input;
        if (row.ScopeKind == "resource" && row.ScopeSpanOrdinal is null && row.SpanOrdinal is null)
        {
            input = LocalRepositorySourceIdentityInput.Resource(
                row.RawRecordId,
                row.ResourceSpanOrdinal,
                row.AttributeOrdinal,
                row.AttributeKey);
        }
        else if (row.ScopeKind == "span" && row.ScopeSpanOrdinal >= 0 && row.SpanOrdinal >= 0)
        {
            input = LocalRepositorySourceIdentityInput.Span(
                row.RawRecordId,
                row.ResourceSpanOrdinal,
                row.ScopeSpanOrdinal.Value,
                row.SpanOrdinal.Value,
                row.AttributeOrdinal,
                row.AttributeKey);
        }
        else
        {
            return false;
        }

        try
        {
            if (LocalRepositoryIdentityHashing.SourceIdentity(input) != row.SourceIdentitySha256)
                return false;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return false;
        }

        if (row.ValueClassification == "admitted")
        {
            return row.LocatorKind == "github_repository"
                && GitHubRepositoryLocatorParser.IsExact(
                    row.CanonicalLocator,
                    row.LocatorSha256,
                    row.DisplayOwner,
                    row.DisplayRepository);
        }

        return row.ValueClassification is "invalid_locator" or "invalid_type" or "duplicate_key"
            && row.LocatorKind is null
            && row.CanonicalLocator is null
            && row.LocatorSha256 is null
            && row.DisplayOwner is null
            && row.DisplayRepository is null;
    }

    private static void ValidateAutomaticAdmissionContexts(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var afterObservationId = string.Empty;
        var afterSessionEventId = string.Empty;
        while (true)
        {
            var page = new List<AutomaticAdmissionContextRow>(AutomaticAdmissionValidationPageSize);
            using (var command = AdmissionCommand(connection, transaction, """
                SELECT c.context_id,c.observation_id,c.context_identity_sha256,
                       c.session_event_id,c.session_id,c.trace_id,c.span_id,c.admission_state,
                       c.repository_id,c.locator_id,c.observed_at,
                       o.source_identity_sha256,o.raw_record_id,o.raw_payload_sha256,
                       o.resource_span_ordinal,o.scope_kind,o.value_classification,
                       o.locator_kind,o.canonical_locator,o.locator_sha256,o.display_owner,
                       o.display_repository,o.source_surface,o.source_application_version,o.observed_at,
                       l.kind,l.canonical_locator,l.locator_sha256,l.source,l.display_owner,l.display_repository
                FROM session_repository_observation_contexts c
                JOIN session_repository_observations o ON o.observation_id=c.observation_id
                LEFT JOIN local_repository_locators l
                  ON l.repository_id=c.repository_id AND l.locator_id=c.locator_id
                WHERE c.observation_id>$after_observation COLLATE BINARY
                   OR (c.observation_id=$after_observation COLLATE BINARY
                       AND c.session_event_id>$after_event COLLATE BINARY)
                ORDER BY c.observation_id COLLATE BINARY,c.session_event_id COLLATE BINARY
                LIMIT 128;
                """))
            {
                command.Parameters.AddWithValue("$after_observation", afterObservationId);
                command.Parameters.AddWithValue("$after_event", afterSessionEventId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    page.Add(ReadContext(reader));
            }

            if (page.Count == 0)
                return;
            foreach (var row in page)
                ValidateAutomaticAdmissionContext(connection, transaction, row);
            var last = page[^1];
            afterObservationId = last.ObservationId;
            afterSessionEventId = last.SessionEventId;
        }
    }

    private static AutomaticAdmissionContextRow ReadContext(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        NullableText(reader, 8), NullableText(reader, 9), reader.GetString(10), reader.GetString(11),
        reader.GetInt64(12), reader.GetString(13), reader.GetInt32(14), reader.GetString(15),
        reader.GetString(16), NullableText(reader, 17), NullableText(reader, 18), NullableText(reader, 19),
        NullableText(reader, 20), NullableText(reader, 21), reader.GetString(22), NullableText(reader, 23),
        reader.GetString(24), NullableText(reader, 25), NullableText(reader, 26), NullableText(reader, 27),
        NullableText(reader, 28), NullableText(reader, 29), NullableText(reader, 30));

    private static void ValidateAutomaticAdmissionContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AutomaticAdmissionContextRow row)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.ContextId)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.ObservationId)
            || !LocalRepositoryCatalogValidation.IsLowerSha256(row.ContextIdentitySha256)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.SessionEventId)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.SessionId)
            || !IsLowerHex(row.TraceId, 32)
            || !IsLowerHex(row.SpanId, 16)
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(row.ObservedAt)
            || row.ObservedAt != row.ObservationObservedAt)
        {
            RejectAutomaticAdmission();
        }
        try
        {
            var expectedIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
                row.SourceIdentitySha256,
                row.SessionId,
                row.SessionEventId,
                row.TraceId,
                row.SpanId));
            if (expectedIdentity != row.ContextIdentitySha256)
                RejectAutomaticAdmission();
        }
        catch (ArgumentException)
        {
            RejectAutomaticAdmission();
        }

        var provenance = new LocalRepositoryCaptureProvenance(
            row.RawRecordId,
            row.RawPayloadSha256,
            row.SourceSurface,
            row.SourceApplicationVersion,
            DateTimeOffset.ParseExact(row.ObservationObservedAt, "O", CultureInfo.InvariantCulture));
        var joined = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            provenance,
            row.TraceId,
            row.SpanId);
        if (joined.Status != LocalRepositorySessionEventJoinStatus.Matched
            || joined.SessionEventId != row.SessionEventId
            || joined.SessionId != row.SessionId)
        {
            RejectAutomaticAdmission();
        }

        var hasHigherPrecedenceSpan = HasHigherPrecedenceSpan(connection, transaction, row);
        if (row.AdmissionState == "shadowed")
        {
            if (row.ObservationScopeKind != "resource"
                || !hasHigherPrecedenceSpan
                || row.RepositoryId is not null
                || row.LocatorId is not null)
            {
                RejectAutomaticAdmission();
            }
            return;
        }
        if (row.AdmissionState != row.ValueClassification
            || row.ObservationScopeKind == "resource" && hasHigherPrecedenceSpan)
        {
            RejectAutomaticAdmission();
        }

        if (row.AdmissionState == "admitted")
        {
            if (row.RepositoryId is null
                || row.LocatorId is null
                || row.ObservationLocatorKind != "github_repository"
                || row.OwnedLocatorKind != row.ObservationLocatorKind
                || row.OwnedCanonicalLocator != row.ObservationCanonicalLocator
                || row.OwnedLocatorSha256 != row.ObservationLocatorSha256
                || row.OwnedLocatorSource is not ("observed" or "manual"))
            {
                RejectAutomaticAdmission();
            }
        }
        else if (row.AdmissionState is not ("invalid_locator" or "invalid_type" or "duplicate_key")
                 || row.RepositoryId is not null
                 || row.LocatorId is not null)
        {
            RejectAutomaticAdmission();
        }
    }

    private static bool HasHigherPrecedenceSpan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AutomaticAdmissionContextRow row) => AdmissionExists(connection, transaction, """
        SELECT 1
        FROM session_repository_observations o
        JOIN session_repository_observation_contexts c ON c.observation_id=o.observation_id
        WHERE o.raw_record_id=$raw_record_id
          AND o.resource_span_ordinal=$resource_span_ordinal
          AND o.scope_kind='span'
          AND c.trace_id=$trace_id COLLATE BINARY
          AND c.span_id=$span_id COLLATE BINARY
        LIMIT 1;
        """,
        ("$raw_record_id", row.RawRecordId),
        ("$resource_span_ordinal", row.ResourceSpanOrdinal),
        ("$trace_id", row.TraceId),
        ("$span_id", row.SpanId));

    private static void ValidateObservedLocatorCreation(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var afterLocatorId = string.Empty;
        while (true)
        {
            var page = new List<ObservedLocatorRow>(AutomaticAdmissionValidationPageSize);
            using (var command = AdmissionCommand(connection, transaction, """
                SELECT l.locator_id,l.repository_id,l.kind,l.canonical_locator,l.locator_sha256,
                       l.source,l.display_owner,l.display_repository,l.created_at,
                       r.display_name,r.revision,r.created_at,h.updated_at
                FROM local_repository_locators l
                JOIN local_repositories r ON r.repository_id=l.repository_id
                LEFT JOIN local_repository_locator_heads h
                  ON h.repository_id=l.repository_id AND h.kind=l.kind
                WHERE l.source='observed'
                  AND l.locator_id>$after_locator COLLATE BINARY
                ORDER BY l.locator_id COLLATE BINARY
                LIMIT 128;
                """))
            {
                command.Parameters.AddWithValue("$after_locator", afterLocatorId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    page.Add(new(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                        reader.GetString(8), reader.GetString(9), reader.GetInt64(10), reader.GetString(11),
                        NullableText(reader, 12)));
                }
            }
            if (page.Count == 0)
                break;
            foreach (var row in page)
            {
                if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.LocatorId)
                    || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.RepositoryId)
                    || row.Kind != "github_repository"
                    || row.Source != "observed"
                    || !GitHubRepositoryLocatorParser.IsExact(
                        row.CanonicalLocator,
                        row.LocatorSha256,
                        row.DisplayOwner,
                        row.DisplayRepository)
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(row.LocatorCreatedAt)
                    || !LocalRepositoryCatalogValidation.IsDisplayName(row.RepositoryDisplayName)
                    || row.RepositoryRevision < 1
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(row.RepositoryCreatedAt)
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(row.HeadUpdatedAt)
                    || row.RepositoryRevision == 1 && row.RepositoryDisplayName != row.DisplayRepository
                    || !HasSingleValidCreateObservedCause(connection, transaction, row))
                {
                    RejectAutomaticAdmission();
                }
            }
            afterLocatorId = page[^1].LocatorId;
        }

        ValidateCreateObservedHistoryPages(connection, transaction);
    }

    private static bool HasSingleValidCreateObservedCause(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ObservedLocatorRow locator)
    {
        using var command = AdmissionCommand(connection, transaction, """
            SELECT h.history_id,h.previous_revision,h.new_revision,h.cause_kind,h.operation_key,
                   h.context_identity_sha256,h.occurred_at,
                   c.context_identity_sha256,c.admission_state,c.repository_id,c.locator_id,
                   o.value_classification,o.locator_kind,o.canonical_locator,o.locator_sha256,
                   o.display_owner,o.display_repository
            FROM local_repository_history h
            LEFT JOIN session_repository_observation_contexts c
              ON c.context_identity_sha256=h.context_identity_sha256
            LEFT JOIN session_repository_observations o ON o.observation_id=c.observation_id
            WHERE h.action='create_observed'
              AND h.repository_id=$repository_id
              AND h.locator_id=$locator_id
            ORDER BY h.history_id COLLATE BINARY
            LIMIT 2;
            """);
        command.Parameters.AddWithValue("$repository_id", locator.RepositoryId);
        command.Parameters.AddWithValue("$locator_id", locator.LocatorId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        var valid = LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(0))
            && reader.GetInt64(1) == 0
            && reader.GetInt64(2) == 1
            && reader.GetString(3) == "source_context"
            && reader.IsDBNull(4)
            && NullableText(reader, 5) is { } causeIdentity
            && LocalRepositoryCatalogValidation.IsLowerSha256(causeIdentity)
            && LocalRepositoryCatalogValidation.IsCanonicalTimestamp(reader.GetString(6))
            && NullableText(reader, 7) == causeIdentity
            && NullableText(reader, 8) == "admitted"
            && NullableText(reader, 9) == locator.RepositoryId
            && NullableText(reader, 10) == locator.LocatorId
            && NullableText(reader, 11) == "admitted"
            && NullableText(reader, 12) == locator.Kind
            && NullableText(reader, 13) == locator.CanonicalLocator
            && NullableText(reader, 14) == locator.LocatorSha256
            && NullableText(reader, 15) == locator.DisplayOwner
            && NullableText(reader, 16) == locator.DisplayRepository;
        return valid && !reader.Read();
    }

    private static void ValidateCreateObservedHistoryPages(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var afterHistoryId = string.Empty;
        while (true)
        {
            var page = new List<CreateObservedHistoryRow>(AutomaticAdmissionValidationPageSize);
            using (var command = AdmissionCommand(connection, transaction, """
                SELECT h.history_id,h.repository_id,h.previous_revision,h.new_revision,h.locator_id,
                       h.cause_kind,h.operation_key,h.context_identity_sha256,h.occurred_at,l.source
                FROM local_repository_history h
                LEFT JOIN local_repository_locators l
                  ON l.repository_id=h.repository_id AND l.locator_id=h.locator_id
                WHERE h.action='create_observed'
                  AND h.history_id>$after_history COLLATE BINARY
                ORDER BY h.history_id COLLATE BINARY
                LIMIT 128;
                """))
            {
                command.Parameters.AddWithValue("$after_history", afterHistoryId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    page.Add(new(
                        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                        NullableText(reader, 4), reader.GetString(5), NullableText(reader, 6),
                        NullableText(reader, 7), reader.GetString(8), NullableText(reader, 9)));
                }
            }
            if (page.Count == 0)
                return;
            foreach (var row in page)
            {
                if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.HistoryId)
                    || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.RepositoryId)
                    || row.PreviousRevision != 0
                    || row.NewRevision != 1
                    || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.LocatorId)
                    || row.CauseKind != "source_context"
                    || row.OperationKey is not null
                    || !LocalRepositoryCatalogValidation.IsLowerSha256(row.ContextIdentitySha256)
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(row.OccurredAt)
                    || row.LocatorSource != "observed"
                    || !AdmissionExists(connection, transaction, """
                        SELECT 1
                        FROM session_repository_observation_contexts c
                        JOIN session_repository_observations o ON o.observation_id=c.observation_id
                        JOIN local_repository_locators l
                          ON l.repository_id=c.repository_id AND l.locator_id=c.locator_id
                        WHERE c.context_identity_sha256=$context_identity
                          AND c.admission_state='admitted'
                          AND c.repository_id=$repository_id
                          AND c.locator_id=$locator_id
                          AND o.value_classification='admitted'
                          AND o.locator_kind=l.kind
                          AND o.canonical_locator=l.canonical_locator
                          AND o.locator_sha256=l.locator_sha256
                          AND o.display_owner=l.display_owner
                          AND o.display_repository=l.display_repository
                        LIMIT 1;
                        """,
                        ("$context_identity", row.ContextIdentitySha256!),
                        ("$repository_id", row.RepositoryId),
                        ("$locator_id", row.LocatorId!)))
                {
                    RejectAutomaticAdmission();
                }
            }
            afterHistoryId = page[^1].HistoryId;
        }
    }

    private static void ValidateAutomaticAdmissionHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryValidatedReconciliationState reconciliationState)
    {
        var afterHistoryId = string.Empty;
        while (true)
        {
            var page = new List<AutomaticReconciliationHistoryRow>(AutomaticAdmissionValidationPageSize);
            using (var command = AdmissionCommand(connection, transaction, """
                SELECT history_id,session_id,previous_state,previous_authority,
                       previous_repository_id,previous_assignment_state_sha256,
                       new_state,new_authority,new_repository_id,new_assignment_state_sha256,
                       reconciliation_fingerprint
                FROM session_repository_assignment_history
                WHERE action='automatic_reconcile'
                  AND history_id>$after_history COLLATE BINARY
                ORDER BY history_id COLLATE BINARY
                LIMIT 128;
                """))
            {
                command.Parameters.AddWithValue("$after_history", afterHistoryId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    page.Add(new(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        NullableText(reader, 4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                        NullableText(reader, 8), reader.GetString(9), reader.GetString(10)));
                }
            }
            if (page.Count == 0)
                return;
            foreach (var row in page)
            {
                if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.HistoryId)
                    || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.SessionId)
                    || !reconciliationState.TryGetCompletedPayloadRawRecordId(
                        row.ReconciliationFingerprint,
                        out var rawRecordId)
                    || AdmissionExists(connection, transaction, """
                        SELECT 1
                        FROM session_repository_assignment_history h
                        WHERE h.action='automatic_reconcile'
                          AND h.session_id=$session_id
                          AND h.reconciliation_fingerprint=$fingerprint
                          AND h.history_id<>$history_id
                        LIMIT 1;
                        """,
                        ("$session_id", row.SessionId),
                        ("$fingerprint", row.ReconciliationFingerprint),
                        ("$history_id", row.HistoryId))
                    || !AdmissionExists(connection, transaction, """
                        SELECT 1
                        FROM session_repository_observation_contexts c
                        JOIN session_repository_observations o ON o.observation_id=c.observation_id
                        WHERE c.session_id=$session_id
                          AND c.admission_state='admitted'
                          AND o.raw_record_id=$raw_record_id
                        LIMIT 1;
                        """,
                        ("$session_id", row.SessionId),
                        ("$raw_record_id", rawRecordId))
                    || !LocalRepositoryAssignmentResolver.IsValidAutomaticReconciliationTransition(
                        row.PreviousState,
                        row.PreviousAuthority,
                        row.PreviousRepositoryId,
                        row.PreviousFingerprint,
                        row.NewState,
                        row.NewAuthority,
                        row.NewRepositoryId,
                        row.NewFingerprint,
                        row.ReconciliationFingerprint))
                {
                    RejectAutomaticAdmission();
                }
            }
            afterHistoryId = page[^1].HistoryId;
        }
    }

    private static SqliteCommand AdmissionCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static bool AdmissionExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = AdmissionCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar() is not null;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RejectAutomaticAdmission() =>
        throw new InvalidOperationException(AutomaticAdmissionStateInvalid);

    private sealed record AutomaticAdmissionObservationRow(
        string ObservationId,
        string SourceIdentitySha256,
        long RawRecordId,
        string RawPayloadSha256,
        int ResourceSpanOrdinal,
        int? ScopeSpanOrdinal,
        int? SpanOrdinal,
        int AttributeOrdinal,
        string ScopeKind,
        string AttributeKey,
        string ValueClassification,
        string? LocatorKind,
        string? CanonicalLocator,
        string? LocatorSha256,
        string? DisplayOwner,
        string? DisplayRepository,
        string SourceSurface,
        string? SourceApplicationVersion,
        string ObservedAt,
        bool HasContext);

    private sealed record AutomaticAdmissionContextRow(
        string ContextId,
        string ObservationId,
        string ContextIdentitySha256,
        string SessionEventId,
        string SessionId,
        string TraceId,
        string SpanId,
        string AdmissionState,
        string? RepositoryId,
        string? LocatorId,
        string ObservedAt,
        string SourceIdentitySha256,
        long RawRecordId,
        string RawPayloadSha256,
        int ResourceSpanOrdinal,
        string ObservationScopeKind,
        string ValueClassification,
        string? ObservationLocatorKind,
        string? ObservationCanonicalLocator,
        string? ObservationLocatorSha256,
        string? ObservationDisplayOwner,
        string? ObservationDisplayRepository,
        string SourceSurface,
        string? SourceApplicationVersion,
        string ObservationObservedAt,
        string? OwnedLocatorKind,
        string? OwnedCanonicalLocator,
        string? OwnedLocatorSha256,
        string? OwnedLocatorSource,
        string? OwnedDisplayOwner,
        string? OwnedDisplayRepository);

    private sealed record ObservedLocatorRow(
        string LocatorId,
        string RepositoryId,
        string Kind,
        string CanonicalLocator,
        string LocatorSha256,
        string Source,
        string DisplayOwner,
        string DisplayRepository,
        string LocatorCreatedAt,
        string RepositoryDisplayName,
        long RepositoryRevision,
        string RepositoryCreatedAt,
        string? HeadUpdatedAt);

    private sealed record CreateObservedHistoryRow(
        string HistoryId,
        string RepositoryId,
        long PreviousRevision,
        long NewRevision,
        string? LocatorId,
        string CauseKind,
        string? OperationKey,
        string? ContextIdentitySha256,
        string OccurredAt,
        string? LocatorSource);

    private sealed record AutomaticReconciliationHistoryRow(
        string HistoryId,
        string SessionId,
        string PreviousState,
        string PreviousAuthority,
        string? PreviousRepositoryId,
        string PreviousFingerprint,
        string NewState,
        string NewAuthority,
        string? NewRepositoryId,
        string NewFingerprint,
        string ReconciliationFingerprint);
}

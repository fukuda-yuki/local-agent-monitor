using System.Globalization;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalRepositoryMutationValidationBuffer
{
    RepositoryHeadPage,
    RepositoryLocatorCollection,
    RepositoryHistoryPage,
    AssignmentHeadPage,
    AssignmentHistoryPage,
    CurrentCandidateCollection,
    ReceiptPage,
    ReceiptHistoryLinkPage,
}

internal enum LocalRepositoryMutationValidationCheckpoint
{
    ProvableRequestFingerprintRecomputed,
}

internal interface ILocalRepositoryMutationValidationObserver
{
    void Materialized(LocalRepositoryMutationValidationBuffer buffer, int count);
    void Reached(LocalRepositoryMutationValidationCheckpoint checkpoint);
}

internal sealed partial class SqliteLocalRepositoryCatalogStore
{
    private const int MutationValidationPageSize = 128;
    private const string MutationStateInvalid = "local_repository_mutation_state_invalid";

    internal static void ValidateRestorableMutationState(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        ValidateRestorableMutationStateCore(connection, transaction, observer: null);

    internal static void ValidateRestorableMutationState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryMutationValidationObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ValidateRestorableMutationStateCore(connection, transaction, observer);
    }

    private static void ValidateRestorableMutationStateCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("local_repository_mutation_state_transaction_mismatch");

        try
        {
            ValidateReceiptState(connection, transaction, observer);
            ValidateRepositoryState(connection, transaction, observer);
            ValidateAssignmentState(connection, transaction, observer);
        }
        catch (SqliteException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidCastException
            or FormatException
            or OverflowException
            or ArgumentException)
        {
            throw new InvalidOperationException(MutationStateInvalid);
        }
    }

    private static void ValidateRepositoryState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        string? after = null;
        while (true)
        {
            var page = new List<RepositoryHead>(MutationValidationPageSize);
            using (var command = Command(connection, transaction, after is null
                ? """
                    SELECT repository_id,display_name,revision,created_at,updated_at
                    FROM local_repositories
                    ORDER BY repository_id COLLATE BINARY
                    LIMIT 128;
                    """
                : """
                    SELECT repository_id,display_name,revision,created_at,updated_at
                    FROM local_repositories
                    WHERE repository_id COLLATE BINARY>$after
                    ORDER BY repository_id COLLATE BINARY
                    LIMIT 128;
                    """))
            {
                if (after is not null)
                    command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    page.Add(new(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetString(4)));
                    observer?.Materialized(LocalRepositoryMutationValidationBuffer.RepositoryHeadPage, page.Count);
                }
            }
            if (page.Count == 0)
                break;
            foreach (var repository in page)
                ValidateRepository(connection, transaction, repository, observer);
            after = page[^1].RepositoryId;
        }

        if (Exists(connection, transaction, """
            SELECT 1 FROM local_repository_locators l
            WHERE NOT EXISTS(SELECT 1 FROM local_repositories r WHERE r.repository_id=l.repository_id)
            UNION ALL
            SELECT 1 FROM local_repository_locator_heads h
            WHERE NOT EXISTS(SELECT 1 FROM local_repositories r WHERE r.repository_id=h.repository_id)
            UNION ALL
            SELECT 1 FROM local_repository_history h
            WHERE NOT EXISTS(SELECT 1 FROM local_repositories r WHERE r.repository_id=h.repository_id)
            UNION ALL
            SELECT 1 FROM local_repository_history
            WHERE previous_revision<0 OR new_revision<1 OR new_revision<>previous_revision+1
            UNION ALL
            SELECT 1 FROM local_repository_locators a
            JOIN local_repository_locators b
              ON b.kind=a.kind AND b.locator_sha256=a.locator_sha256 AND b.locator_id<>a.locator_id
            LIMIT 1;
            """))
        {
            Reject();
        }
    }

    private static void ValidateRepository(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryHead repository,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repository.RepositoryId)
            || !LocalRepositoryCatalogValidation.IsDisplayName(repository.DisplayName)
            || repository.Revision < 1
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(repository.CreatedAt)
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(repository.UpdatedAt))
        {
            Reject();
        }

        var locators = ReadLocators(connection, transaction, repository.RepositoryId, observer);
        string? locatorHead = null;
        using (var command = Command(connection, transaction, """
            SELECT kind,locator_id,updated_at
            FROM local_repository_locator_heads
            WHERE repository_id=$repository_id;
            """))
        {
            command.Parameters.AddWithValue("$repository_id", repository.RepositoryId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != "github_repository"
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(reader.GetString(2)))
                {
                    Reject();
                }
                locatorHead = reader.GetString(1);
                if (!locators.ContainsKey(locatorHead) || reader.Read())
                    Reject();
            }
        }

        long chainRevision = 0;
        string? chainLocator = null;
        var referencedLocators = new HashSet<string>(StringComparer.Ordinal);
        string? createOperationKey = null;
        string? createLocator = null;
        string? latestRenameOperationKey = null;
        long latestRenamePreviousRevision = 0;
        var afterHistoryRevision = 0L;
        while (true)
        {
            var historyPage = new List<RepositoryHistoryRow>(MutationValidationPageSize);
            using (var command = Command(connection, transaction, """
                SELECT history_id,action,previous_revision,new_revision,locator_id,cause_kind,
                       operation_key,context_identity_sha256,occurred_at
                FROM local_repository_history
                WHERE repository_id=$repository_id AND new_revision>$after_revision
                ORDER BY new_revision
                LIMIT 128;
                """))
            {
                command.Parameters.AddWithValue("$repository_id", repository.RepositoryId);
                command.Parameters.AddWithValue("$after_revision", afterHistoryRevision);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    historyPage.Add(new(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.GetString(8)));
                    observer?.Materialized(LocalRepositoryMutationValidationBuffer.RepositoryHistoryPage, historyPage.Count);
                }
            }
            if (historyPage.Count == 0)
                break;
            foreach (var history in historyPage)
            {
                var historyId = history.HistoryId;
                var action = history.Action;
                var previousRevision = history.PreviousRevision;
                var newRevision = history.NewRevision;
                var locatorId = history.LocatorId;
                var causeKind = history.CauseKind;
                var operationKey = history.OperationKey;
                var contextIdentity = history.ContextIdentity;
                var occurredAt = history.OccurredAt;
                if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(historyId)
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(occurredAt)
                    || previousRevision != chainRevision
                    || newRevision != chainRevision + 1)
                {
                    Reject();
                }

                var userOperation = action is "create" or "rename" or "add_locator" or "replace_locator";
                if (userOperation)
                {
                    if (causeKind != "user_operation"
                        || operationKey is null
                        || !LocalRepositoryCatalogValidation.IsOperationKey(operationKey)
                        || contextIdentity is not null)
                    {
                        Reject();
                    }
                    _ = ReadReceiptFingerprint(connection, transaction, operationKey!);
                }
                else if (action == "create_observed")
                {
                    if (causeKind != "source_context"
                        || operationKey is not null
                        || !LocalRepositoryCatalogValidation.IsLowerSha256(contextIdentity))
                    {
                        Reject();
                    }
                }
                else
                {
                    Reject();
                }

                switch (action)
                {
                    case "create":
                        if (chainRevision != 0 || newRevision != 1)
                            Reject();
                        if (locatorId is not null)
                        {
                            var locator = RequiredLocator(locators, locatorId!);
                            if (locator.Source != "manual")
                                Reject();
                            referencedLocators.Add(locatorId);
                            createLocator = locator.CanonicalLocator;
                        }
                        chainLocator = locatorId;
                        createOperationKey = operationKey;
                        break;
                    case "create_observed":
                        if (chainRevision != 0 || newRevision != 1 || locatorId is null)
                            Reject();
                        if (RequiredLocator(locators, locatorId!).Source != "observed")
                            Reject();
                        referencedLocators.Add(locatorId!);
                        chainLocator = locatorId;
                        break;
                    case "rename":
                        if (chainRevision < 1 || locatorId is not null)
                            Reject();
                        latestRenameOperationKey = operationKey;
                        latestRenamePreviousRevision = previousRevision;
                        break;
                    case "add_locator":
                        if (chainRevision < 1 || chainLocator is not null || locatorId is null)
                            Reject();
                        {
                            var locator = RequiredLocator(locators, locatorId!);
                            if (locator.Source != "manual")
                                Reject();
                            if (!HasExpectedReceiptFingerprint(
                                connection,
                                transaction,
                                operationKey!,
                                LocalRepositoryOperationFingerprint.SetGitHubLocator(repository.RepositoryId, previousRevision, locator.CanonicalLocator),
                                observer))
                            {
                                Reject();
                            }
                        }
                        referencedLocators.Add(locatorId!);
                        chainLocator = locatorId;
                        break;
                    case "replace_locator":
                        if (chainRevision < 1 || chainLocator is null || locatorId is null || locatorId == chainLocator)
                            Reject();
                        {
                            var locator = RequiredLocator(locators, locatorId!);
                            if (!HasExpectedReceiptFingerprint(
                                connection,
                                transaction,
                                operationKey!,
                                LocalRepositoryOperationFingerprint.SetGitHubLocator(repository.RepositoryId, previousRevision, locator.CanonicalLocator),
                                observer))
                            {
                                Reject();
                            }
                        }
                        referencedLocators.Add(locatorId!);
                        chainLocator = locatorId;
                        break;
                }
                chainRevision = newRevision;
            }
            afterHistoryRevision = historyPage[^1].NewRevision;
        }

        if (chainRevision != repository.Revision
            || !string.Equals(chainLocator, locatorHead, StringComparison.Ordinal)
            || !referencedLocators.SetEquals(locators.Keys))
        {
            Reject();
        }
        if (latestRenameOperationKey is null)
        {
            if (createOperationKey is not null)
            {
                if (!HasExpectedReceiptFingerprint(
                    connection,
                    transaction,
                    createOperationKey,
                    LocalRepositoryOperationFingerprint.Create(repository.DisplayName, createLocator),
                    observer))
                {
                    Reject();
                }
            }
        }
        else
        {
            if (!HasExpectedReceiptFingerprint(
                connection,
                transaction,
                latestRenameOperationKey,
                LocalRepositoryOperationFingerprint.Rename(repository.RepositoryId, latestRenamePreviousRevision, repository.DisplayName),
                observer))
            {
                Reject();
            }
        }
    }

    private static Dictionary<string, LocatorRow> ReadLocators(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryId,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        var locators = new Dictionary<string, LocatorRow>(StringComparer.Ordinal);
        using var command = Command(connection, transaction, """
            SELECT locator_id,kind,canonical_locator,locator_sha256,source,display_owner,display_repository,created_at
            FROM local_repository_locators
            WHERE repository_id=$repository_id
            ORDER BY created_at,locator_id COLLATE BINARY
            LIMIT 129;
            """);
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (locators.Count == MutationValidationPageSize)
                Reject();
            var locator = new LocatorRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7));
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(locator.LocatorId)
                || locator.Kind != "github_repository"
                || locator.Source is not ("manual" or "observed")
                || !GitHubRepositoryLocatorParser.IsExact(
                    locator.CanonicalLocator,
                    locator.LocatorSha256,
                    locator.DisplayOwner,
                    locator.DisplayRepository)
                || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(locator.CreatedAt)
                || !locators.TryAdd(locator.LocatorId, locator))
            {
                Reject();
            }
            observer?.Materialized(LocalRepositoryMutationValidationBuffer.RepositoryLocatorCollection, locators.Count);
        }
        return locators;
    }

    private static void ValidateAssignmentState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        string? after = null;
        while (true)
        {
            var page = new List<string>(MutationValidationPageSize);
            using (var command = Command(connection, transaction, after is null
                ? """
                    SELECT session_id FROM (
                        SELECT session_id COLLATE BINARY AS session_id
                        FROM session_repository_assignment_revisions
                        UNION
                        SELECT session_id COLLATE BINARY AS session_id
                        FROM session_repository_observation_contexts
                        WHERE admission_state='admitted'
                    )
                    ORDER BY session_id COLLATE BINARY
                    LIMIT 128;
                    """
                : """
                    SELECT session_id FROM (
                        SELECT session_id COLLATE BINARY AS session_id
                        FROM session_repository_assignment_revisions
                        UNION
                        SELECT session_id COLLATE BINARY AS session_id
                        FROM session_repository_observation_contexts
                        WHERE admission_state='admitted'
                    )
                    WHERE session_id COLLATE BINARY>$after
                    ORDER BY session_id COLLATE BINARY
                    LIMIT 128;
                    """))
            {
                if (after is not null)
                    command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    page.Add(reader.GetString(0));
                    observer?.Materialized(LocalRepositoryMutationValidationBuffer.AssignmentHeadPage, page.Count);
                }
            }
            if (page.Count == 0)
                break;
            foreach (var sessionId in page)
                ValidateAssignment(connection, transaction, sessionId, observer);
            after = page[^1];
        }

        if (Exists(connection, transaction, """
            SELECT 1 FROM session_repository_assignment_history h
            WHERE NOT EXISTS(
                SELECT 1 FROM session_repository_assignment_revisions r
                WHERE r.session_id=h.session_id AND r.revision>=1)
            UNION ALL
            SELECT 1 FROM session_repository_manual_overrides o
            WHERE NOT EXISTS(
                SELECT 1 FROM session_repository_assignment_revisions r
                WHERE r.session_id=o.session_id AND r.revision>=1)
            UNION ALL
            SELECT 1 FROM session_repository_assignment_history
            WHERE previous_revision<0 OR new_revision<1 OR new_revision<>previous_revision+1
            LIMIT 1;
            """))
        {
            Reject();
        }
    }

    private static void ValidateAssignment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
            || !Exists(connection, transaction, "SELECT 1 FROM sessions WHERE session_id=$value LIMIT 1;", sessionId))
        {
            Reject();
        }

        var revision = 0L;
        using (var command = Command(connection, transaction, """
            SELECT revision,updated_at
            FROM session_repository_assignment_revisions
            WHERE session_id=$session_id;
            """))
        {
            command.Parameters.AddWithValue("$session_id", sessionId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                revision = reader.GetInt64(0);
                if (revision < 1
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(reader.GetString(1))
                    || reader.Read())
                {
                    Reject();
                }
            }
        }

        var chainRevision = 0L;
        var chainState = "unassigned";
        var chainAuthority = "none";
        string? chainRepositoryId = null;
        var chainFingerprint = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(new(
            chainState,
            chainAuthority,
            chainRepositoryId,
            []));
        var afterRevision = 0L;
        while (true)
        {
            var page = new List<AssignmentHistoryRow>(MutationValidationPageSize);
            using (var command = Command(connection, transaction, """
                SELECT history_id,action,previous_revision,new_revision,
                       previous_assignment_state_sha256,new_assignment_state_sha256,
                       previous_state,new_state,previous_authority,new_authority,
                       previous_repository_id,new_repository_id,cause_kind,operation_key,
                       reconciliation_fingerprint,occurred_at
                FROM session_repository_assignment_history
                WHERE session_id=$session_id AND new_revision>$after_revision
                ORDER BY new_revision
                LIMIT 128;
                """))
            {
                command.Parameters.AddWithValue("$session_id", sessionId);
                command.Parameters.AddWithValue("$after_revision", afterRevision);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    page.Add(new(
                        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                        reader.GetString(8), reader.GetString(9),
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.GetString(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13),
                        reader.IsDBNull(14) ? null : reader.GetString(14),
                        reader.GetString(15)));
                    observer?.Materialized(LocalRepositoryMutationValidationBuffer.AssignmentHistoryPage, page.Count);
                }
            }
            if (page.Count == 0)
                break;
            foreach (var row in page)
            {
                if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(row.HistoryId)
                    || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(row.OccurredAt)
                    || row.PreviousRevision != chainRevision
                    || row.NewRevision != chainRevision + 1
                    || !string.Equals(row.PreviousState, chainState, StringComparison.Ordinal)
                    || !string.Equals(row.PreviousAuthority, chainAuthority, StringComparison.Ordinal)
                    || !string.Equals(row.PreviousRepositoryId, chainRepositoryId, StringComparison.Ordinal)
                    || !string.Equals(row.PreviousFingerprint, chainFingerprint, StringComparison.Ordinal)
                    || !LocalRepositoryAssignmentResolver.IsValidHistoricalAssignmentEndpoint(
                        row.PreviousState,
                        row.PreviousAuthority,
                        row.PreviousRepositoryId,
                        row.PreviousFingerprint)
                    || !LocalRepositoryAssignmentResolver.IsValidHistoricalAssignmentEndpoint(
                        row.NewState,
                        row.NewAuthority,
                        row.NewRepositoryId,
                        row.NewFingerprint)
                    || string.Equals(row.PreviousFingerprint, row.NewFingerprint, StringComparison.Ordinal))
                {
                    Reject();
                }

                if (row.Action == "automatic_reconcile")
                {
                    if (row.CauseKind != "source_reconciliation"
                        || row.OperationKey is not null
                        || row.ReconciliationFingerprint is null
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
                        Reject();
                    }
                }
                else
                {
                    if (row.CauseKind != "user_operation"
                        || row.OperationKey is null
                        || !LocalRepositoryCatalogValidation.IsOperationKey(row.OperationKey)
                        || row.ReconciliationFingerprint is not null)
                    {
                        Reject();
                    }
                    var actionValid = row.Action switch
                    {
                        "assign" => row.NewState == "assigned" && row.NewAuthority == "manual" && row.NewRepositoryId is not null,
                        "explicitly_unassign" => row.NewState == "explicitly_unassigned" && row.NewAuthority == "manual" && row.NewRepositoryId is null,
                        "resume_automatic" => row.PreviousAuthority == "manual" && row.NewAuthority != "manual",
                        _ => false,
                    };
                    if (!actionValid)
                    {
                        Reject();
                    }
                    if (!HasExpectedReceiptFingerprint(
                        connection,
                        transaction,
                        row.OperationKey!,
                        LocalRepositoryOperationFingerprint.SessionAction(
                            sessionId,
                            row.PreviousRevision,
                            row.Action,
                            row.Action == "assign" ? row.NewRepositoryId : null),
                        observer))
                    {
                        Reject();
                    }
                }

                chainRevision = row.NewRevision;
                chainState = row.NewState;
                chainAuthority = row.NewAuthority;
                chainRepositoryId = row.NewRepositoryId;
                chainFingerprint = row.NewFingerprint;
            }
            afterRevision = page[^1].NewRevision;
        }

        if (chainRevision != revision)
            Reject();
        if (observer is null)
        {
            LocalRepositoryAssignmentResolver.ValidateCurrentResolverHead(
                connection,
                transaction,
                sessionId,
                revision,
                chainState,
                chainAuthority,
                chainRepositoryId,
                chainFingerprint);
        }
        else
        {
            LocalRepositoryAssignmentResolver.ValidateCurrentResolverHead(
                connection,
                transaction,
                sessionId,
                revision,
                chainState,
                chainAuthority,
                chainRepositoryId,
                chainFingerprint,
                observer);
        }
    }

    private static void ValidateReceiptState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        if (Exists(connection, transaction, $"""
            SELECT 1 FROM local_repository_operation_receipts
            WHERE typeof(response_entity)<>'blob'
               OR length(response_entity)>{LocalRepositoryExactResponse.MaximumEntityBytes}
            LIMIT 1;
            """))
        {
            Reject();
        }

        string? after = null;
        while (true)
        {
            var receipts = new List<ReceiptRow>(MutationValidationPageSize);
            using (var command = Command(connection, transaction, after is null
                ? """
                    SELECT operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at
                    FROM local_repository_operation_receipts
                    ORDER BY operation_key COLLATE BINARY
                    LIMIT 128;
                    """
                : """
                    SELECT operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at
                    FROM local_repository_operation_receipts
                    WHERE operation_key COLLATE BINARY>$after
                    ORDER BY operation_key COLLATE BINARY
                    LIMIT 128;
                    """))
            {
                if (after is not null)
                    command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    receipts.Add(new(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetFieldValue<byte[]>(5),
                        reader.GetString(6)));
                    observer?.Materialized(LocalRepositoryMutationValidationBuffer.ReceiptPage, receipts.Count);
                }
            }
            if (receipts.Count == 0)
                break;

            var links = ReadReceiptLinks(connection, transaction, receipts, observer);
            foreach (var receipt in receipts)
                ValidateReceipt(connection, transaction, receipt, links);
            after = receipts[^1].OperationKey;
        }
    }

    private static List<ReceiptLink> ReadReceiptLinks(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ReceiptRow> receipts,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        var parameters = string.Join(',', Enumerable.Range(0, receipts.Count).Select(static index => $"$key{index}"));
        using var command = Command(connection, transaction, $"""
            SELECT operation_key,entity_kind,target_id,new_revision,action,new_state FROM (
                SELECT operation_key,'repository' AS entity_kind,repository_id AS target_id,new_revision,action,NULL AS new_state
                FROM local_repository_history WHERE operation_key IN ({parameters})
                UNION ALL
                SELECT operation_key,'assignment' AS entity_kind,session_id AS target_id,new_revision,action,new_state
                FROM session_repository_assignment_history WHERE operation_key IN ({parameters})
            )
            ORDER BY operation_key COLLATE BINARY,entity_kind COLLATE BINARY,target_id COLLATE BINARY,new_revision
            LIMIT 129;
            """);
        for (var index = 0; index < receipts.Count; index++)
            command.Parameters.AddWithValue($"$key{index}", receipts[index].OperationKey);
        var links = new List<ReceiptLink>(MutationValidationPageSize);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (links.Count == MutationValidationPageSize)
                Reject();
            links.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
            observer?.Materialized(LocalRepositoryMutationValidationBuffer.ReceiptHistoryLinkPage, links.Count);
        }
        return links;
    }

    private static void ValidateReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReceiptRow receipt,
        IReadOnlyList<ReceiptLink> links)
    {
        if (!LocalRepositoryCatalogValidation.IsOperationKey(receipt.OperationKey)
            || !LocalRepositoryCatalogValidation.IsLowerSha256(receipt.RequestFingerprint)
            || receipt.StatusCode is not (200 or 201)
            || receipt.ContentType != LocalRepositoryExactResponse.SuccessContentType
            || receipt.CacheControl != LocalRepositoryExactResponse.SuccessCacheControl
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(receipt.CreatedAt))
        {
            Reject();
        }
        var decoded = LocalRepositoryExactResponse.ValidateMutationEntity(receipt.StatusCode, receipt.ResponseEntity);
        var matching = links.Where(link => link.OperationKey == receipt.OperationKey).ToArray();
        if (matching.Length > 1)
            Reject();
        if (matching.Length == 1)
        {
            var link = matching[0];
            if (link.EntityKind == "repository")
            {
                var expectedStatus = link.Action == "create" ? 201 : 200;
                if (link.Action is not ("create" or "rename" or "add_locator" or "replace_locator")
                    || receipt.StatusCode != expectedStatus
                    || decoded.Kind != LocalRepositoryMutationEntityKind.Repository
                    || decoded.TargetId != link.TargetId
                    || decoded.Revision != link.NewRevision
                    || decoded.State is not null)
                {
                    Reject();
                }
            }
            else if (link.EntityKind == "assignment")
            {
                if (link.Action is not ("assign" or "explicitly_unassign" or "resume_automatic")
                    || receipt.StatusCode != 200
                    || decoded.Kind != LocalRepositoryMutationEntityKind.Assignment
                    || decoded.TargetId != link.TargetId
                    || decoded.Revision != link.NewRevision
                    || decoded.State != link.NewState)
                {
                    Reject();
                }
            }
            else
            {
                Reject();
            }
            return;
        }

        if (receipt.StatusCode != 200)
            Reject();
        if (decoded.Kind == LocalRepositoryMutationEntityKind.Repository)
        {
            using var command = Command(connection, transaction, """
                SELECT revision FROM local_repositories WHERE repository_id=$target_id;
                """);
            command.Parameters.AddWithValue("$target_id", decoded.TargetId);
            var current = command.ExecuteScalar();
            if (current is null
                || decoded.Revision < 1
                || decoded.Revision > Convert.ToInt64(current, CultureInfo.InvariantCulture)
                || !Exists(connection, transaction, """
                    SELECT 1 FROM local_repository_history
                    WHERE repository_id=$target_id AND new_revision=$revision LIMIT 1;
                    """, decoded.TargetId, decoded.Revision))
            {
                Reject();
            }
            return;
        }

        if (!Exists(connection, transaction, "SELECT 1 FROM sessions WHERE session_id=$value LIMIT 1;", decoded.TargetId))
            Reject();
        var currentRevision = ReadLogicalAssignmentRevision(connection, transaction, decoded.TargetId);
        if (decoded.Revision < 0 || decoded.Revision > currentRevision)
            Reject();
        if (decoded.Revision == 0)
        {
            if (decoded.State != "unassigned")
                Reject();
        }
        else if (!Exists(connection, transaction, """
            SELECT 1 FROM session_repository_assignment_history
            WHERE session_id=$target_id AND new_revision=$revision AND new_state=$state LIMIT 1;
            """, decoded.TargetId, decoded.Revision, decoded.State!))
        {
            Reject();
        }
    }

    private static long ReadLogicalAssignmentRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = Command(connection, transaction, """
            SELECT revision FROM session_repository_assignment_revisions WHERE session_id=$session_id;
            """);
        command.Parameters.AddWithValue("$session_id", sessionId);
        var value = command.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string ReadReceiptFingerprint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey)
    {
        using var command = Command(connection, transaction, """
            SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key=$operation_key;
            """);
        command.Parameters.AddWithValue("$operation_key", operationKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException(MutationStateInvalid);
        var fingerprint = reader.GetString(0);
        if (reader.Read() || !LocalRepositoryCatalogValidation.IsLowerSha256(fingerprint))
            Reject();
        return fingerprint;
    }

    private static bool HasExpectedReceiptFingerprint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        string expectedFingerprint,
        ILocalRepositoryMutationValidationObserver? observer)
    {
        var actualFingerprint = ReadReceiptFingerprint(connection, transaction, operationKey);
        observer?.Reached(LocalRepositoryMutationValidationCheckpoint.ProvableRequestFingerprintRecomputed);
        return string.Equals(actualFingerprint, expectedFingerprint, StringComparison.Ordinal);
    }

    private static LocatorRow RequiredLocator(IReadOnlyDictionary<string, LocatorRow> locators, string locatorId) =>
        locators.TryGetValue(locatorId, out var locator) ? locator : throw new InvalidOperationException(MutationStateInvalid);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar() is not null;
    }

    private static bool Exists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string value)
    {
        using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$value", value);
        return command.ExecuteScalar() is not null;
    }

    private static bool Exists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string targetId,
        long revision,
        string? state = null)
    {
        using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$target_id", targetId);
        command.Parameters.AddWithValue("$revision", revision);
        if (state is not null)
            command.Parameters.AddWithValue("$state", state);
        return command.ExecuteScalar() is not null;
    }

    private static void Reject() => throw new InvalidOperationException(MutationStateInvalid);

    private sealed record RepositoryHead(string RepositoryId, string DisplayName, long Revision, string CreatedAt, string UpdatedAt);
    private sealed record LocatorRow(
        string LocatorId,
        string Kind,
        string CanonicalLocator,
        string LocatorSha256,
        string Source,
        string DisplayOwner,
        string DisplayRepository,
        string CreatedAt);
    private sealed record RepositoryHistoryRow(
        string HistoryId,
        string Action,
        long PreviousRevision,
        long NewRevision,
        string? LocatorId,
        string CauseKind,
        string? OperationKey,
        string? ContextIdentity,
        string OccurredAt);
    private sealed record AssignmentHistoryRow(
        string HistoryId,
        string Action,
        long PreviousRevision,
        long NewRevision,
        string PreviousFingerprint,
        string NewFingerprint,
        string PreviousState,
        string NewState,
        string PreviousAuthority,
        string NewAuthority,
        string? PreviousRepositoryId,
        string? NewRepositoryId,
        string CauseKind,
        string? OperationKey,
        string? ReconciliationFingerprint,
        string OccurredAt);
    private sealed record ReceiptRow(
        string OperationKey,
        string RequestFingerprint,
        int StatusCode,
        string ContentType,
        string CacheControl,
        byte[] ResponseEntity,
        string CreatedAt);
    private sealed record ReceiptLink(
        string OperationKey,
        string EntityKind,
        string TargetId,
        long NewRevision,
        string Action,
        string? NewState);
}

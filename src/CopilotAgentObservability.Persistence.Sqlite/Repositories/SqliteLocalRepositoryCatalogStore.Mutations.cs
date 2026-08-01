using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteLocalRepositoryCatalogStore
{
    private const int MaximumLocatorCount = 128;

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        LocalRepositoryCreateStoreInput mutation,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationRepository> writeEntity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.Validate(201);
        if (!LocalRepositoryCatalogValidation.IsDisplayName(mutation.DisplayName)
            || (mutation.Locator is not null && !GitHubRepositoryLocatorParser.IsExact(mutation.Locator))
            || !LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey))
            throw new InvalidOperationException("local_repository_store_input_invalid");
        ArgumentNullException.ThrowIfNull(writeEntity);
        var callbackActive = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var receipt = ReadReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, 201);
            if (receipt is not null)
            {
                transaction.Commit();
                return ValueTask.FromResult(receipt);
            }

            if (mutation.Locator is not null && ReadLocatorOwner(connection, transaction, mutation.Locator) is not null)
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.LocatorConflict));

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var timestamp = Timestamp(now);
            var repositoryId = NextId(now);
            var historyId = NextId(now);
            string? locatorId = null;
            if (mutation.Locator is not null)
                locatorId = NextId(now);
            InsertManualRepository(connection, transaction, repositoryId, mutation.DisplayName, timestamp);
            if (mutation.Locator is not null)
            {
                InsertManualLocator(connection, transaction, locatorId!, repositoryId, mutation.Locator, timestamp);
                InsertLocatorHead(connection, transaction, repositoryId, locatorId!, timestamp);
            }
            InsertRepositoryHistory(connection, transaction, historyId, repositoryId, "create", 0, 1, locatorId, validOperationKey, timestamp);
            var snapshot = new LocalRepositoryMutationRepository(repositoryId, mutation.DisplayName, 1, now, now);
            callbackActive = true;
            var entity = writeEntity(snapshot);
            callbackActive = false;
            var response = LocalRepositoryExactResponse.CreateSuccess(201, entity);
            InsertReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, response, timestamp);
            transaction.Commit();
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationSucceeded(response, false));
        }
        catch (SqliteException exception) when (!callbackActive && exception.SqliteErrorCode is 5 or 6)
        {
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationBusy());
        }
    }

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        LocalRepositoryRenameStoreInput mutation,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationRepository> writeEntity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.Validate(200);
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(mutation.RepositoryId)
            || mutation.ExpectedRevision < 1
            || !LocalRepositoryCatalogValidation.IsDisplayName(mutation.DisplayName)
            || !LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey))
            throw new InvalidOperationException("local_repository_store_input_invalid");
        ArgumentNullException.ThrowIfNull(writeEntity);
        var callbackActive = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var receipt = ReadReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, 200);
            if (receipt is not null)
            {
                transaction.Commit();
                return ValueTask.FromResult(receipt);
            }
            var frontier = ReadRepositoryFrontier(connection, transaction, mutation.RepositoryId);
            if (frontier is null)
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.RepositoryNotFound));
            if (frontier.Revision != mutation.ExpectedRevision)
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.RevisionConflict));

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            if (!string.Equals(frontier.DisplayName, mutation.DisplayName, StringComparison.Ordinal))
            {
                var nextRevision = checked(frontier.Revision + 1);
                var timestamp = Timestamp(now);
                UpdateRepository(connection, transaction, mutation.RepositoryId, frontier.Revision, mutation.DisplayName, nextRevision, timestamp);
                InsertRepositoryHistory(connection, transaction, NextId(now), mutation.RepositoryId, "rename", frontier.Revision, nextRevision, null, validOperationKey, timestamp);
                frontier = frontier with { DisplayName = mutation.DisplayName, Revision = nextRevision, UpdatedAt = now };
            }
            var snapshot = RepositorySnapshot(frontier);
            callbackActive = true;
            var entity = writeEntity(snapshot);
            callbackActive = false;
            var response = LocalRepositoryExactResponse.CreateSuccess(200, entity);
            InsertReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, response, Timestamp(now));
            transaction.Commit();
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationSucceeded(response, false));
        }
        catch (SqliteException exception) when (!callbackActive && exception.SqliteErrorCode is 5 or 6)
        {
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationBusy());
        }
    }

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        LocalRepositorySetLocatorStoreInput mutation,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationRepository> writeEntity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.Validate(200);
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(mutation.RepositoryId)
            || mutation.ExpectedRevision < 1
            || !GitHubRepositoryLocatorParser.IsExact(mutation.Locator)
            || !LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey))
            throw new InvalidOperationException("local_repository_store_input_invalid");
        ArgumentNullException.ThrowIfNull(writeEntity);
        var callbackActive = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var receipt = ReadReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, 200);
            if (receipt is not null)
            {
                transaction.Commit();
                return ValueTask.FromResult(receipt);
            }
            var frontier = ReadRepositoryFrontier(connection, transaction, mutation.RepositoryId);
            if (frontier is null)
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.RepositoryNotFound));
            if (frontier.Revision != mutation.ExpectedRevision)
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.RevisionConflict));

            var owner = ReadLocatorOwner(connection, transaction, mutation.Locator);
            if (owner is not null && !string.Equals(owner.RepositoryId, mutation.RepositoryId, StringComparison.Ordinal))
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.LocatorConflict));

            var current = frontier.CurrentLocatorId is null
                ? null
                : frontier.Locators.Single(locator => locator.LocatorId == frontier.CurrentLocatorId);
            var target = owner is null
                ? null
                : frontier.Locators.SingleOrDefault(locator => locator.LocatorId == owner.LocatorId);
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            if (current is null || !string.Equals(current.LocatorSha256, mutation.Locator.LocatorSha256, StringComparison.Ordinal))
            {
                if (owner is null && frontier.Locators.Count >= MaximumLocatorCount)
                    return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.LocatorLimitReached));
                var timestamp = Timestamp(now);
                var locatorId = target?.LocatorId ?? NextId(now);
                if (target is null)
                    InsertManualLocator(connection, transaction, locatorId, mutation.RepositoryId, mutation.Locator, timestamp);
                if (frontier.CurrentLocatorId is null)
                    InsertLocatorHead(connection, transaction, mutation.RepositoryId, locatorId, timestamp);
                else
                    UpdateLocatorHead(connection, transaction, mutation.RepositoryId, frontier.CurrentLocatorId, locatorId, timestamp);
                var nextRevision = checked(frontier.Revision + 1);
                UpdateRepository(connection, transaction, mutation.RepositoryId, frontier.Revision, frontier.DisplayName, nextRevision, timestamp);
                InsertRepositoryHistory(
                    connection,
                    transaction,
                    NextId(now),
                    mutation.RepositoryId,
                    frontier.CurrentLocatorId is null ? "add_locator" : "replace_locator",
                    frontier.Revision,
                    nextRevision,
                    locatorId,
                    validOperationKey,
                    timestamp);
                frontier = frontier with { Revision = nextRevision, UpdatedAt = now, CurrentLocatorId = locatorId };
            }
            var snapshot = RepositorySnapshot(frontier);
            callbackActive = true;
            var entity = writeEntity(snapshot);
            callbackActive = false;
            var response = LocalRepositoryExactResponse.CreateSuccess(200, entity);
            InsertReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, response, Timestamp(now));
            transaction.Commit();
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationSucceeded(response, false));
        }
        catch (SqliteException exception) when (!callbackActive && exception.SqliteErrorCode is 5 or 6)
        {
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationBusy());
        }
    }

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        LocalRepositorySessionActionStoreInput mutation,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationAssignment> writeEntity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.Validate(200);
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(mutation.SessionId)
            || mutation.ExpectedRevision < 0
            || !LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey)
            || !ValidSessionStoreInput(mutation))
            throw new InvalidOperationException("local_repository_store_input_invalid");
        ArgumentNullException.ThrowIfNull(writeEntity);
        var callbackActive = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var receipt = ReadReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, 200);
            if (receipt is not null)
            {
                transaction.Commit();
                return ValueTask.FromResult(receipt);
            }
            if (!TargetExists(connection, transaction, "sessions", "session_id", mutation.SessionId))
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.SessionNotFound));
            if (mutation.RepositoryId is not null && !TargetExists(connection, transaction, "local_repositories", "repository_id", mutation.RepositoryId))
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.RepositoryNotFound));
            if (ReadAssignmentRevision(connection, transaction, mutation.SessionId) != mutation.ExpectedRevision)
                return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.RevisionConflict));

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var previous = assignmentResolver.ReadCurrent(connection, transaction, mutation.SessionId);
            var transition = assignmentResolver.ApplyManual(
                connection,
                transaction,
                mutation.SessionId,
                mutation.ExpectedRevision,
                mutation.Action,
                mutation.RepositoryId,
                validOperationKey,
                now);
            var current = transition.Current;
            var snapshot = new LocalRepositoryMutationAssignment(
                transition.SessionId,
                transition.NewRevision,
                current.State,
                current.Authority,
                current.RepositoryId,
                current.State == "conflict"
                    ? Array.AsReadOnly(current.CandidateRepositoryIds.ToArray())
                    : Array.AsReadOnly(Array.Empty<string>()),
                transition.RevisionChanged ? now : previous.UpdatedAt);
            callbackActive = true;
            var entity = writeEntity(snapshot);
            callbackActive = false;
            var response = LocalRepositoryExactResponse.CreateSuccess(200, entity);
            InsertReceipt(connection, transaction, validOperationKey, mutation.RequestFingerprint, response, Timestamp(now));
            transaction.Commit();
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationSucceeded(response, false));
        }
        catch (SqliteException exception) when (!callbackActive && exception.SqliteErrorCode is 5 or 6)
        {
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationBusy());
        }
    }

    internal async ValueTask<LocalRepositoryLocatorReadResult> ReadLocatorsAsync(
        string repositoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RepositoryFrontier frontier;
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                frontier = ReadRepositoryFrontier(connection, transaction, repositoryId)!;
                if (frontier is null)
                    return new LocalRepositoryLocatorRepositoryNotFound();
                transaction.Commit();
            }
            var retentionContext = RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
            var rawStore = new RawTelemetryStore(databasePath, retentionContext, timeProvider);
            var rawAvailability = new LocalRepositoryRawAvailabilityReader(rawStore, retentionContext);
            if (!binding.Matches(rawAvailability.Binding))
                throw new InvalidOperationException("local_repository_store_binding_mismatch");
            var items = new List<LocalRepositoryLocatorItem>(frontier.Locators.Count);
            foreach (var locator in frontier.Locators
                .OrderByDescending(item => string.Equals(item.LocatorId, frontier.CurrentLocatorId, StringComparison.Ordinal))
                .ThenBy(static item => item.CreatedAt)
                .ThenBy(static item => item.LocatorId, StringComparer.Ordinal))
            {
                LocalRepositoryObservedLocatorProvenance? projected = null;
                if (locator.Source == "observed")
                {
                    if (!frontier.ObservedProvenance.TryGetValue(locator.LocatorId, out var raw))
                        return new LocalRepositoryLocatorReadCorrupt();
                    string availabilityValue;
                    locatorReadCheckpoint?.Reached(LocalRepositoryLocatorReadCheckpoint.BeforeAvailabilityRead);
                    await using (var availability = await rawAvailability.ReadAsync(raw.RawRecordId, raw.RawPayloadSha256, RetentionReadKind.Access, cancellationToken).ConfigureAwait(false))
                    {
                        if (availability.Status == LocalRepositoryRawAvailabilityStatus.Success
                            && string.Equals(availability.Availability, LocalRepositoryRawAvailability.Available, StringComparison.Ordinal)
                            && availability.Lease is not null)
                        {
                            locatorReadCheckpoint?.Reached(LocalRepositoryLocatorReadCheckpoint.AfterAvailabilityLeaseAcquired);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        switch (availability.Status)
                        {
                            case LocalRepositoryRawAvailabilityStatus.Success:
                                availabilityValue = availability.Availability!;
                                break;
                            case LocalRepositoryRawAvailabilityStatus.Busy:
                                return new LocalRepositoryLocatorReadBusy();
                            case LocalRepositoryRawAvailabilityStatus.PayloadDigestMismatch:
                            case LocalRepositoryRawAvailabilityStatus.Corrupt:
                                return new LocalRepositoryLocatorReadCorrupt();
                            default:
                                throw new InvalidOperationException("local_repository_raw_availability_invalid");
                        }
                    }
                    projected = new(raw.SourceSurface, raw.SourceApplicationVersion, raw.TraceId, raw.SpanId, raw.ObservedAt, availabilityValue);
                }
                else if (frontier.ObservedProvenance.ContainsKey(locator.LocatorId))
                {
                    return new LocalRepositoryLocatorReadCorrupt();
                }
                items.Add(new(
                    locator.LocatorId,
                    "github_repository",
                    locator.CanonicalLocator,
                    locator.DisplayOwner,
                    locator.DisplayRepository,
                    locator.Source,
                    string.Equals(locator.LocatorId, frontier.CurrentLocatorId, StringComparison.Ordinal),
                    locator.CreatedAt,
                    projected));
            }
            return new LocalRepositoryLocatorsFound(new(
                frontier.RepositoryId,
                frontier.Revision,
                Array.AsReadOnly(items.ToArray())));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new LocalRepositoryLocatorReadBusy();
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            return new LocalRepositoryLocatorReadCorrupt();
        }
    }

    internal ValueTask<LocalRepositoryAssignmentReadResult> ReadAssignmentAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!TargetExists(connection, transaction, "sessions", "session_id", sessionId))
                return ValueTask.FromResult<LocalRepositoryAssignmentReadResult>(new LocalRepositoryAssignmentSessionNotFound());
            var snapshot = assignmentResolver.ReadCurrent(connection, transaction, sessionId);
            transaction.Commit();
            return ValueTask.FromResult<LocalRepositoryAssignmentReadResult>(new LocalRepositoryAssignmentFound(snapshot));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return ValueTask.FromResult<LocalRepositoryAssignmentReadResult>(new LocalRepositoryAssignmentReadBusy());
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            return ValueTask.FromResult<LocalRepositoryAssignmentReadResult>(new LocalRepositoryAssignmentReadCorrupt());
        }
    }

    private static LocalRepositoryMutationResult? ReadReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        string requestFingerprint,
        int expectedStatusCode)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_fingerprint,status_code,content_type,cache_control,response_entity
            FROM local_repository_operation_receipts
            WHERE operation_key=$operation_key;
            """;
        command.Parameters.AddWithValue("$operation_key", operationKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var response = LocalRepositoryExactResponse.FromStored(
            expectedStatusCode,
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<byte[]>(4));
        var storedFingerprint = reader.GetString(0);
        if (reader.Read())
            throw new InvalidOperationException("local_repository_receipt_duplicate");
        if (!LocalRepositoryCatalogValidation.IsLowerSha256(storedFingerprint))
            throw new InvalidOperationException("local_repository_receipt_fingerprint_corrupt");
        return string.Equals(storedFingerprint, requestFingerprint, StringComparison.Ordinal)
            ? new LocalRepositoryMutationSucceeded(response, true)
            : new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.IdempotencyConflict);
    }

    private static void InsertReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        string requestFingerprint,
        LocalRepositoryExactResponse response,
        string createdAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at)
            VALUES($operation_key,$request_fingerprint,$status_code,$content_type,$cache_control,$response_entity,$created_at);
            """;
        command.Parameters.AddWithValue("$operation_key", operationKey);
        command.Parameters.AddWithValue("$request_fingerprint", requestFingerprint);
        command.Parameters.AddWithValue("$status_code", response.StatusCode);
        command.Parameters.AddWithValue("$content_type", response.ContentType);
        command.Parameters.AddWithValue("$cache_control", response.CacheControl);
        command.Parameters.Add("$response_entity", SqliteType.Blob).Value = response.CopyEntity();
        command.Parameters.AddWithValue("$created_at", createdAt);
        command.ExecuteNonQuery();
    }

    private static RepositoryFrontier? ReadRepositoryFrontier(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryId)
    {
        string displayName;
        long revision;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT repository_id,display_name,revision,created_at,updated_at FROM local_repositories WHERE repository_id=$repository_id;";
            command.Parameters.AddWithValue("$repository_id", repositoryId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var storedRepositoryId = reader.GetString(0);
            displayName = reader.GetString(1);
            revision = reader.GetInt64(2);
            var createdAtValue = reader.GetString(3);
            var updatedAtValue = reader.GetString(4);
            if (!string.Equals(storedRepositoryId, repositoryId, StringComparison.Ordinal)
                || !LocalRepositoryCatalogValidation.IsDisplayName(displayName)
                || revision < 1
                || !TryTimestamp(createdAtValue, out createdAt)
                || !TryTimestamp(updatedAtValue, out updatedAt)
                || reader.Read())
                throw new InvalidOperationException("local_repository_frontier_corrupt");
        }

        var locators = new List<RepositoryLocatorRow>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT locator_id,kind,canonical_locator,locator_sha256,source,display_owner,display_repository,created_at
                FROM local_repository_locators
                WHERE repository_id=$repository_id
                ORDER BY created_at COLLATE BINARY,locator_id COLLATE BINARY
                LIMIT 129;
                """;
            command.Parameters.AddWithValue("$repository_id", repositoryId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(0))
                    || reader.GetString(1) != "github_repository"
                    || reader.GetString(4) is not ("manual" or "observed")
                    || !HasExactLocator(reader.GetString(2), reader.GetString(3), reader.GetString(5), reader.GetString(6))
                    || !TryTimestamp(reader.GetString(7), out var locatorCreatedAt))
                    throw new InvalidOperationException("local_repository_frontier_corrupt");
                locators.Add(new(reader.GetString(0), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), locatorCreatedAt));
            }
        }
        if (locators.Count > MaximumLocatorCount)
            throw new InvalidOperationException("local_repository_frontier_corrupt");

        string? currentLocatorId = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT kind,locator_id FROM local_repository_locator_heads WHERE repository_id=$repository_id;";
            command.Parameters.AddWithValue("$repository_id", repositoryId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != "github_repository"
                    || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(1))
                    || locators.Count(locator => locator.LocatorId == reader.GetString(1)) != 1)
                    throw new InvalidOperationException("local_repository_frontier_corrupt");
                currentLocatorId = reader.GetString(1);
                if (reader.Read())
                    throw new InvalidOperationException("local_repository_frontier_corrupt");
            }
        }

        ValidateRepositoryHistory(connection, transaction, repositoryId, revision, locators, currentLocatorId);
        var observedProvenance = ReadObservedProvenance(connection, transaction, repositoryId, locators);
        return new(repositoryId, displayName, revision, createdAt, updatedAt, currentLocatorId, locators.AsReadOnly(), observedProvenance);
    }

    private static void ValidateRepositoryHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryId,
        long repositoryRevision,
        IReadOnlyList<RepositoryLocatorRow> locators,
        string? currentLocatorId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT h.history_id,h.action,h.previous_revision,h.new_revision,h.locator_id,h.cause_kind,
                   h.operation_key,h.context_identity_sha256,h.occurred_at,r.operation_key
            FROM local_repository_history h
            LEFT JOIN local_repository_operation_receipts r ON r.operation_key=h.operation_key
            WHERE h.repository_id=$repository_id
            ORDER BY h.new_revision;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        using var reader = command.ExecuteReader();
        long chainRevision = 0;
        string? effectiveLocatorId = null;
        var referencedLocatorIds = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var action = reader.GetString(1);
            var previousRevision = reader.GetInt64(2);
            var newRevision = reader.GetInt64(3);
            var locatorId = reader.IsDBNull(4) ? null : reader.GetString(4);
            var causeKind = reader.GetString(5);
            var operationKey = reader.IsDBNull(6) ? null : reader.GetString(6);
            var contextIdentity = reader.IsDBNull(7) ? null : reader.GetString(7);
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(0))
                || previousRevision != chainRevision
                || newRevision != chainRevision + 1
                || !TryTimestamp(reader.GetString(8), out _)
                || !ValidHistoryCause(action, causeKind, operationKey, contextIdentity)
                || (causeKind == "user_operation" && reader.IsDBNull(9))
                || !ValidHistoryLocator(action, locatorId, locators))
                throw new InvalidOperationException("local_repository_history_corrupt");
            var locator = locatorId is null ? null : locators.Single(item => item.LocatorId == locatorId);
            effectiveLocatorId = NextHistoryLocator(newRevision, action, locator, effectiveLocatorId);
            if (locatorId is not null)
                referencedLocatorIds.Add(locatorId);
            chainRevision = newRevision;
        }
        if (chainRevision != repositoryRevision
            || !string.Equals(effectiveLocatorId, currentLocatorId, StringComparison.Ordinal)
            || (locators.Count == 0) != (currentLocatorId is null)
            || !referencedLocatorIds.SetEquals(locators.Select(static locator => locator.LocatorId)))
            throw new InvalidOperationException("local_repository_history_corrupt");
    }

    private static string? NextHistoryLocator(
        long newRevision,
        string action,
        RepositoryLocatorRow? locator,
        string? effectiveLocatorId)
    {
        if (newRevision == 1)
        {
            return (action, locator) switch
            {
                ("create", null) => null,
                ("create", { Source: "manual" }) => locator.LocatorId,
                ("create_observed", { Source: "observed" }) => locator.LocatorId,
                _ => throw new InvalidOperationException("local_repository_history_corrupt"),
            };
        }

        return (action, locator) switch
        {
            ("rename", null) => effectiveLocatorId,
            ("add_locator", { Source: "manual" }) when effectiveLocatorId is null => locator.LocatorId,
            ("replace_locator", { Source: "manual" or "observed" })
                when effectiveLocatorId is not null && !string.Equals(effectiveLocatorId, locator.LocatorId, StringComparison.Ordinal) => locator.LocatorId,
            _ => throw new InvalidOperationException("local_repository_history_corrupt"),
        };
    }

    private static bool ValidHistoryCause(string action, string causeKind, string? operationKey, string? contextIdentity) =>
        action == "create_observed"
            ? causeKind == "source_context" && operationKey is null && LocalRepositoryCatalogValidation.IsLowerSha256(contextIdentity)
            : action is "create" or "rename" or "add_locator" or "replace_locator"
              && causeKind == "user_operation" && LocalRepositoryCatalogValidation.IsOperationKey(operationKey!) && contextIdentity is null;

    private static bool ValidHistoryLocator(string action, string? locatorId, IReadOnlyList<RepositoryLocatorRow> locators) => action switch
    {
        "create" => locatorId is null || locators.Count(locator => locator.LocatorId == locatorId) == 1,
        "create_observed" or "add_locator" or "replace_locator" => locatorId is not null && locators.Count(locator => locator.LocatorId == locatorId) == 1,
        "rename" => locatorId is null,
        _ => false,
    };

    private static LocatorOwner? ReadLocatorOwner(SqliteConnection connection, SqliteTransaction transaction, GitHubRepositoryLocator locator)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT locator_id,repository_id,kind,canonical_locator,locator_sha256
            FROM local_repository_locators
            WHERE kind='github_repository' AND locator_sha256=$locator_sha256;
            """;
        command.Parameters.AddWithValue("$locator_sha256", locator.LocatorSha256);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(0))
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(1))
            || reader.GetString(2) != "github_repository"
            || !string.Equals(reader.GetString(3), locator.CanonicalLocator, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), locator.LocatorSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("local_repository_locator_owner_corrupt");
        var owner = new LocatorOwner(reader.GetString(0), reader.GetString(1));
        if (reader.Read())
            throw new InvalidOperationException("local_repository_locator_owner_corrupt");
        return owner;
    }

    private static Dictionary<string, ObservedProvenanceRow> ReadObservedProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryId,
        IReadOnlyList<RepositoryLocatorRow> locators)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT h.locator_id,h.context_identity_sha256,
                   c.repository_id,c.locator_id,c.trace_id,c.span_id,c.observed_at,
                   o.source_surface,o.source_application_version,o.raw_record_id,o.raw_payload_sha256,
                   o.source_identity_sha256,c.session_id,c.session_event_id,c.context_identity_sha256,
                   o.resource_span_ordinal,o.scope_span_ordinal,o.span_ordinal,o.attribute_ordinal,
                   o.scope_kind,o.attribute_key,o.value_classification,o.locator_kind,
                   o.canonical_locator,o.locator_sha256,o.display_owner,o.display_repository,o.observed_at,
                   c.admission_state,o.observation_id,c.context_id,e.trace_id
            FROM local_repository_history h
            JOIN session_repository_observation_contexts c
              ON c.context_identity_sha256=h.context_identity_sha256
            JOIN session_repository_observations o ON o.observation_id=c.observation_id
            JOIN session_events e ON e.session_id=c.session_id AND e.event_id=c.session_event_id
            WHERE h.repository_id=$repository_id AND h.action='create_observed';
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, ObservedProvenanceRow>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var locatorId = reader.GetString(0);
            var locator = locators.SingleOrDefault(item => item.LocatorId == locatorId && item.Source == "observed");
            if (locator is null
                || !string.Equals(reader.GetString(2), repositoryId, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(3), locatorId, StringComparison.Ordinal)
                || !LocalRepositoryCatalogValidation.IsLowerSha256(reader.GetString(1))
                || !string.Equals(reader.GetString(1), reader.GetString(14), StringComparison.Ordinal)
                || !LocalRepositoryCatalogValidation.IsLowerSha256(reader.GetString(10))
                || reader.GetString(7) is not ("github-copilot-cli" or "github-copilot-vscode")
                || !IsVisibleVersion(reader, 8)
                || !TryTimestamp(reader.GetString(6), out var observedAt)
                || !TryTimestamp(reader.GetString(27), out var observationObservedAt)
                || observedAt != observationObservedAt
                || !HasExpectedSourceIdentity(reader)
                || reader.GetString(21) != "admitted"
                || reader.GetString(22) != "github_repository"
                || !string.Equals(reader.GetString(23), locator.CanonicalLocator, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(24), locator.LocatorSha256, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(25), locator.DisplayOwner, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(26), locator.DisplayRepository, StringComparison.Ordinal)
                || reader.GetString(28) != "admitted"
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(29))
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(30))
                || !string.Equals(reader.GetString(31), reader.GetString(4), StringComparison.Ordinal)
                || !HasExpectedContextIdentity(reader)
                || rows.ContainsKey(locatorId))
                throw new InvalidOperationException("local_repository_provenance_corrupt");
            rows.Add(locatorId, new(
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(4),
                reader.GetString(5),
                observedAt,
                reader.GetInt64(9),
                reader.GetString(10)));
        }
        if (rows.Count != locators.Count(locator => locator.Source == "observed"))
            throw new InvalidOperationException("local_repository_provenance_corrupt");
        return rows;
    }

    private static bool HasExpectedContextIdentity(SqliteDataReader reader)
    {
        try
        {
            var expected = LocalRepositoryIdentityHashing.ContextIdentity(new(
                reader.GetString(11), reader.GetString(12), reader.GetString(13), reader.GetString(4), reader.GetString(5)));
            return string.Equals(expected, reader.GetString(14), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    private static bool HasExpectedSourceIdentity(SqliteDataReader reader)
    {
        try
        {
            var input = reader.GetString(19) switch
            {
                "resource" when reader.IsDBNull(16) && reader.IsDBNull(17) =>
                    LocalRepositorySourceIdentityInput.Resource(reader.GetInt64(9), reader.GetInt32(15), reader.GetInt32(18), reader.GetString(20)),
                "span" when !reader.IsDBNull(16) && !reader.IsDBNull(17) =>
                    LocalRepositorySourceIdentityInput.Span(reader.GetInt64(9), reader.GetInt32(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18), reader.GetString(20)),
                _ => throw new ArgumentException("Invalid persisted observation scope."),
            };
            return string.Equals(
                reader.GetString(11),
                LocalRepositoryIdentityHashing.SourceIdentity(input),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    private static bool IsVisibleVersion(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
        || reader.GetString(ordinal) is { Length: >= 1 and <= 64 } value
           && value.All(static character => character is >= '!' and <= '~' && character is not '/' and not '\\');

    private static void InsertManualRepository(SqliteConnection connection, SqliteTransaction transaction, string repositoryId, string displayName, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES($repository_id,$display_name,1,$created_at,$updated_at);";
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        command.Parameters.AddWithValue("$display_name", displayName);
        command.Parameters.AddWithValue("$created_at", timestamp);
        command.Parameters.AddWithValue("$updated_at", timestamp);
        command.ExecuteNonQuery();
    }

    private static void InsertManualLocator(SqliteConnection connection, SqliteTransaction transaction, string locatorId, string repositoryId, GitHubRepositoryLocator locator, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_repository_locators(locator_id,repository_id,kind,canonical_locator,locator_sha256,source,display_owner,display_repository,created_at)
            VALUES($locator_id,$repository_id,'github_repository',$canonical_locator,$locator_sha256,'manual',$display_owner,$display_repository,$created_at);
            """;
        command.Parameters.AddWithValue("$locator_id", locatorId);
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        command.Parameters.AddWithValue("$canonical_locator", locator.CanonicalLocator);
        command.Parameters.AddWithValue("$locator_sha256", locator.LocatorSha256);
        command.Parameters.AddWithValue("$display_owner", locator.DisplayOwner);
        command.Parameters.AddWithValue("$display_repository", locator.DisplayRepository);
        command.Parameters.AddWithValue("$created_at", timestamp);
        command.ExecuteNonQuery();
    }

    private static void InsertLocatorHead(SqliteConnection connection, SqliteTransaction transaction, string repositoryId, string locatorId, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO local_repository_locator_heads(repository_id,kind,locator_id,updated_at) VALUES($repository_id,'github_repository',$locator_id,$updated_at);";
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        command.Parameters.AddWithValue("$locator_id", locatorId);
        command.Parameters.AddWithValue("$updated_at", timestamp);
        command.ExecuteNonQuery();
    }

    private static void UpdateLocatorHead(SqliteConnection connection, SqliteTransaction transaction, string repositoryId, string previousLocatorId, string locatorId, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE local_repository_locator_heads SET locator_id=$locator_id,updated_at=$updated_at WHERE repository_id=$repository_id AND kind='github_repository' AND locator_id=$previous_locator_id;";
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        command.Parameters.AddWithValue("$previous_locator_id", previousLocatorId);
        command.Parameters.AddWithValue("$locator_id", locatorId);
        command.Parameters.AddWithValue("$updated_at", timestamp);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("local_repository_locator_head_stale");
    }

    private static void UpdateRepository(SqliteConnection connection, SqliteTransaction transaction, string repositoryId, long previousRevision, string displayName, long nextRevision, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE local_repositories SET display_name=$display_name,revision=$next_revision,updated_at=$updated_at WHERE repository_id=$repository_id AND revision=$previous_revision;";
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        command.Parameters.AddWithValue("$previous_revision", previousRevision);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        command.Parameters.AddWithValue("$display_name", displayName);
        command.Parameters.AddWithValue("$updated_at", timestamp);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("local_repository_revision_stale");
    }

    private static void InsertRepositoryHistory(SqliteConnection connection, SqliteTransaction transaction, string historyId, string repositoryId, string action, long previousRevision, long newRevision, string? locatorId, string operationKey, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_repository_history(history_id,repository_id,action,previous_revision,new_revision,locator_id,cause_kind,operation_key,context_identity_sha256,occurred_at)
            VALUES($history_id,$repository_id,$action,$previous_revision,$new_revision,$locator_id,'user_operation',$operation_key,NULL,$occurred_at);
            """;
        command.Parameters.AddWithValue("$history_id", historyId);
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$previous_revision", previousRevision);
        command.Parameters.AddWithValue("$new_revision", newRevision);
        command.Parameters.AddWithValue("$locator_id", locatorId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$operation_key", operationKey);
        command.Parameters.AddWithValue("$occurred_at", timestamp);
        command.ExecuteNonQuery();
    }

    private static bool TargetExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {column}=$value);";
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static long ReadAssignmentRevision(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM session_repository_assignment_revisions WHERE session_id=$session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        var value = command.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static LocalRepositoryMutationRepository RepositorySnapshot(RepositoryFrontier frontier) =>
        new(frontier.RepositoryId, frontier.DisplayName, frontier.Revision, frontier.CreatedAt, frontier.UpdatedAt);

    private static bool ValidSessionStoreInput(LocalRepositorySessionActionStoreInput mutation) => mutation.Action switch
    {
        LocalRepositorySessionAction.Assign => mutation.ActionValue == "assign" && LocalRepositoryCatalogValidation.IsCanonicalUuidV7(mutation.RepositoryId),
        LocalRepositorySessionAction.ExplicitlyUnassign => mutation.ActionValue == "explicitly_unassign" && mutation.RepositoryId is null,
        LocalRepositorySessionAction.ResumeAutomatic => mutation.ActionValue == "resume_automatic" && mutation.RepositoryId is null,
        _ => false,
    };

    private static bool HasExactLocator(string canonicalLocator, string locatorSha256, string displayOwner, string displayRepository) =>
        GitHubRepositoryLocatorParser.IsExact(canonicalLocator, locatorSha256, displayOwner, displayRepository);

    private static bool TryTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (LocalRepositoryCatalogValidation.IsCanonicalTimestamp(value))
        {
            timestamp = DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
            return true;
        }
        timestamp = default;
        return false;
    }

    private sealed record LocatorOwner(string LocatorId, string RepositoryId);
    private sealed record RepositoryLocatorRow(
        string LocatorId,
        string CanonicalLocator,
        string LocatorSha256,
        string Source,
        string DisplayOwner,
        string DisplayRepository,
        DateTimeOffset CreatedAt);
    private sealed record RepositoryFrontier(
        string RepositoryId,
        string DisplayName,
        long Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? CurrentLocatorId,
        IReadOnlyList<RepositoryLocatorRow> Locators,
        IReadOnlyDictionary<string, ObservedProvenanceRow> ObservedProvenance);
    private sealed record ObservedProvenanceRow(
        string SourceSurface,
        string? SourceApplicationVersion,
        string TraceId,
        string SpanId,
        DateTimeOffset ObservedAt,
        long RawRecordId,
        string RawPayloadSha256);
}

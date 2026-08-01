using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalRepositoryAssignmentReconcileStatus
{
    Applied,
    CardinalityExceeded,
}

internal sealed class LocalRepositoryProspectiveAssignmentContext
{
    internal LocalRepositoryProspectiveAssignmentContext(
        string contextId,
        string contextIdentitySha256,
        string sessionId,
        string repositoryId,
        string locatorId)
    {
        ContextId = contextId;
        ContextIdentitySha256 = contextIdentitySha256;
        SessionId = sessionId;
        RepositoryId = repositoryId;
        LocatorId = locatorId;
    }

    internal string ContextId { get; }
    internal string ContextIdentitySha256 { get; }
    internal string SessionId { get; }
    internal string RepositoryId { get; }
    internal string LocatorId { get; }
}

internal sealed record LocalRepositoryAutomaticAssignmentResolution(
    string SessionId,
    long PreviousRevision,
    long NewRevision,
    string State,
    string Authority,
    string? RepositoryId,
    IReadOnlyList<string> AutomaticCandidateRepositoryIds,
    string PreviousAssignmentStateSha256,
    string NewAssignmentStateSha256,
    bool RevisionChanged);

internal sealed record LocalRepositoryAssignmentReconcileResult(
    LocalRepositoryAssignmentReconcileStatus Status,
    IReadOnlyList<LocalRepositoryAutomaticAssignmentResolution> Resolutions,
    string? RejectedSessionId);

internal sealed record LocalRepositoryManualAssignmentTransition(
    string SessionId,
    long PreviousRevision,
    long NewRevision,
    LocalRepositoryEffectiveAssignment Previous,
    LocalRepositoryEffectiveAssignment Current,
    bool RevisionChanged);

internal sealed class LocalRepositoryAssignmentResolver
{
    private const int MaximumCandidateCount = 128;
    private static readonly object PreparationSeal = new();
    private readonly Func<DateTimeOffset, string> historyIdFactory;

    internal LocalRepositoryAssignmentResolver()
        : this(static occurredAt => Guid.CreateVersion7(occurredAt).ToString("D", CultureInfo.InvariantCulture))
    {
    }

    internal LocalRepositoryAssignmentResolver(Func<DateTimeOffset, string> historyIdFactory)
    {
        ArgumentNullException.ThrowIfNull(historyIdFactory);
        this.historyIdFactory = historyIdFactory;
    }

    internal AutomaticPreparation PrepareAutomatic(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        IReadOnlyCollection<string> affectedSessionIds,
        IReadOnlyCollection<LocalRepositoryProspectiveAssignmentContext> prospectiveAdmittedContexts,
        string reconciliationFingerprint,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(affectedSessionIds);
        ArgumentNullException.ThrowIfNull(prospectiveAdmittedContexts);
        if (rawRecordId < 1)
            throw new ArgumentOutOfRangeException(nameof(rawRecordId));
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("local_repository_assignment_transaction_mismatch");
        if (!LocalRepositoryCatalogValidation.IsLowerSha256(reconciliationFingerprint))
            throw new ArgumentException("The reconciliation fingerprint is invalid.", nameof(reconciliationFingerprint));
        var occurredAtText = occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (!LocalRepositoryCatalogValidation.IsCanonicalTimestamp(occurredAtText))
            throw new ArgumentOutOfRangeException(nameof(occurredAt));

        var prospectiveBySession = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var prospectiveByIdentity = new Dictionary<string, ExpectedContext>(StringComparer.Ordinal);
        var prospectiveById = new Dictionary<string, ExpectedContext>(StringComparer.Ordinal);
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sessionId in affectedSessionIds)
        {
            ValidateUuid(sessionId, nameof(affectedSessionIds));
            sessions.Add(sessionId);
        }
        foreach (var context in prospectiveAdmittedContexts)
        {
            ArgumentNullException.ThrowIfNull(context);
            ValidateUuid(context.ContextId, nameof(prospectiveAdmittedContexts));
            if (!LocalRepositoryCatalogValidation.IsLowerSha256(context.ContextIdentitySha256))
                throw new ArgumentException("Context identity must be lowercase SHA-256 hexadecimal.", nameof(prospectiveAdmittedContexts));
            ValidateUuid(context.SessionId, nameof(prospectiveAdmittedContexts));
            ValidateUuid(context.RepositoryId, nameof(prospectiveAdmittedContexts));
            ValidateUuid(context.LocatorId, nameof(prospectiveAdmittedContexts));
            var expected = new ExpectedContext(
                context.ContextId,
                context.ContextIdentitySha256,
                context.SessionId,
                context.RepositoryId,
                context.LocatorId);
            if (prospectiveByIdentity.TryGetValue(expected.ContextIdentitySha256, out var sameIdentity))
            {
                if (sameIdentity != expected)
                    throw new ArgumentException("A context identity has conflicting prospective ownership.", nameof(prospectiveAdmittedContexts));
                continue;
            }
            if (prospectiveById.TryGetValue(expected.ContextId, out var sameId) && sameId != expected)
                throw new ArgumentException("A context ID has conflicting prospective identity.", nameof(prospectiveAdmittedContexts));
            prospectiveByIdentity.Add(expected.ContextIdentitySha256, expected);
            prospectiveById.Add(expected.ContextId, expected);
            sessions.Add(context.SessionId);
            if (!prospectiveBySession.TryGetValue(context.SessionId, out var candidates))
            {
                candidates = new(StringComparer.Ordinal);
                prospectiveBySession.Add(context.SessionId, candidates);
            }
            candidates.Add(context.RepositoryId);
        }

        var prepared = new List<LocalRepositoryPreparedAssignmentResolution>(sessions.Count);
        var expectedFrontiers = new List<ExpectedSessionFrontier>(sessions.Count);
        foreach (var sessionId in sessions.OrderBy(static value => value, StringComparer.Ordinal))
        {
            var previousContexts = ReadAdmittedContextsForSession(connection, transaction, sessionId);
            var previousCandidates = previousContexts
                .Select(static context => context.RepositoryId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            var expectedFrontier = previousContexts.ToDictionary(
                static context => context.ContextIdentitySha256,
                StringComparer.Ordinal);
            foreach (var prospectiveContext in prospectiveByIdentity.Values.Where(context => context.SessionId == sessionId))
            {
                if (expectedFrontier.TryGetValue(prospectiveContext.ContextIdentitySha256, out var existingContext)
                    && existingContext != prospectiveContext)
                {
                    throw new InvalidOperationException("local_repository_assignment_context_frontier_mismatch");
                }
                expectedFrontier[prospectiveContext.ContextIdentitySha256] = prospectiveContext;
            }
            expectedFrontiers.Add(new(
                sessionId,
                Array.AsReadOnly(expectedFrontier.Values
                    .OrderBy(static context => context.ContextIdentitySha256, StringComparer.Ordinal)
                    .ThenBy(static context => context.ContextId, StringComparer.Ordinal)
                    .ToArray())));
            var candidates = new HashSet<string>(previousCandidates, StringComparer.Ordinal);
            if (prospectiveBySession.TryGetValue(sessionId, out var prospective))
                candidates.UnionWith(prospective);
            var orderedCandidates = candidates.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
            if (orderedCandidates.Length > MaximumCandidateCount)
            {
                return AutomaticPreparation.Create(
                    LocalRepositoryAssignmentReconcileStatus.CardinalityExceeded,
                    sessionId,
                    PreparationSeal,
                    this,
                    connection,
                    transaction,
                    rawRecordId,
                    prospectiveByIdentity.Values
                        .OrderBy(static context => context.ContextIdentitySha256, StringComparer.Ordinal)
                        .ThenBy(static context => context.ContextId, StringComparer.Ordinal)
                        .ToArray(),
                    [],
                    [],
                    reconciliationFingerprint,
                    occurredAt,
                    occurredAtText);
            }

            var revision = ReadRevision(connection, transaction, sessionId);
            var manual = ReadManualOverride(connection, transaction, sessionId);
            if (manual is not null)
            {
                var current = ReadManualChainHead(connection, transaction, sessionId, revision, manual);
                var currentFingerprint = Fingerprint(current);
                prepared.Add(new(
                    sessionId,
                    revision,
                    current,
                    current,
                    orderedCandidates,
                    currentFingerprint,
                    currentFingerprint,
                    manual));
                continue;
            }

            var previous = revision == 0 ? ResolveAutomatic([]) : ResolveAutomatic(previousCandidates);
            var next = ResolveAutomatic(orderedCandidates);
            prepared.Add(new(
                sessionId,
                revision,
                previous,
                next,
                orderedCandidates,
                Fingerprint(previous),
                Fingerprint(next),
                null));
        }

        return AutomaticPreparation.Create(
            LocalRepositoryAssignmentReconcileStatus.Applied,
            null,
            PreparationSeal,
            this,
            connection,
            transaction,
            rawRecordId,
            prospectiveByIdentity.Values
                .OrderBy(static context => context.ContextIdentitySha256, StringComparer.Ordinal)
                .ThenBy(static context => context.ContextId, StringComparer.Ordinal)
                .ToArray(),
            expectedFrontiers,
            prepared,
            reconciliationFingerprint,
            occurredAt,
            occurredAtText);
    }

    internal LocalRepositoryAssignmentReconcileResult ApplyAutomatic(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AutomaticPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(preparation);
        var prepared = preparation.Unseal(PreparationSeal);
        if (!ReferenceEquals(transaction.Connection, connection)
            || !ReferenceEquals(prepared.Owner, this)
            || !ReferenceEquals(prepared.Connection, connection)
            || !ReferenceEquals(prepared.Transaction, transaction))
        {
            throw new InvalidOperationException("local_repository_assignment_transaction_mismatch");
        }
        if (preparation.Status == LocalRepositoryAssignmentReconcileStatus.CardinalityExceeded)
        {
            return new(preparation.Status, [], preparation.RejectedSessionId);
        }

        ValidateDurableContextGraph(connection, transaction, prepared);
        foreach (var expectedFrontier in prepared.ExpectedSessionFrontiers)
        {
            var actualFrontier = ReadAdmittedContextsForSession(connection, transaction, expectedFrontier.SessionId);
            if (!actualFrontier.SequenceEqual(expectedFrontier.Contexts))
                throw new InvalidOperationException("local_repository_assignment_context_frontier_mismatch");
        }
        var verifiedResolutions = new List<LocalRepositoryPreparedAssignmentResolution>(prepared.Resolutions.Count);
        foreach (var resolution in prepared.Resolutions)
        {
            var actualCandidates = ReadExistingCandidates(connection, transaction, resolution.SessionId);
            if (actualCandidates.Length > MaximumCandidateCount)
                throw new InvalidOperationException("local_repository_assignment_cardinality_exceeded");
            if (!actualCandidates.SequenceEqual(resolution.CandidateRepositoryIds, StringComparer.Ordinal))
                throw new InvalidOperationException("local_repository_assignment_candidate_frontier_mismatch");
            var currentManualOverride = ReadManualOverride(connection, transaction, resolution.SessionId);
            if (currentManualOverride != resolution.ManualOverride)
                throw new InvalidOperationException("local_repository_assignment_manual_override_stale");
            ValidateCurrentAssignmentChain(connection, transaction, resolution);
            var verifiedNext = currentManualOverride is null
                ? ResolveAutomatic(actualCandidates)
                : resolution.Previous;
            var verifiedNextFingerprint = Fingerprint(verifiedNext);
            if (!string.Equals(verifiedNextFingerprint, resolution.NextFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("local_repository_assignment_preparation_invalid");
            verifiedResolutions.Add(resolution with
            {
                Next = verifiedNext,
                CandidateRepositoryIds = Array.AsReadOnly(actualCandidates),
                NextFingerprint = verifiedNextFingerprint,
            });
        }

        var pendingWrites = new List<(LocalRepositoryPreparedAssignmentResolution Resolution, string HistoryId)>();
        foreach (var resolution in verifiedResolutions.Where(static item => item.RevisionChanged))
        {
            var historyId = historyIdFactory(prepared.OccurredAt);
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(historyId))
                throw new InvalidOperationException("local_repository_assignment_history_id_invalid");
            pendingWrites.Add((resolution, historyId));
        }

        foreach (var pendingWrite in pendingWrites)
        {
            var resolution = pendingWrite.Resolution;
            var nextRevision = checked(resolution.Revision + 1);
            AdvanceRevision(connection, transaction, resolution.SessionId, resolution.Revision, nextRevision, prepared.OccurredAtText);
            AppendHistory(
                connection,
                transaction,
                resolution,
                nextRevision,
                pendingWrite.HistoryId,
                prepared.ReconciliationFingerprint,
                prepared.OccurredAtText);
        }

        return new(
            LocalRepositoryAssignmentReconcileStatus.Applied,
            verifiedResolutions.Select(static item => new LocalRepositoryAutomaticAssignmentResolution(
                item.SessionId,
                item.Revision,
                item.RevisionChanged ? item.Revision + 1 : item.Revision,
                item.Next.State,
                item.Next.Authority,
                item.Next.RepositoryId,
                item.CandidateRepositoryIds,
                item.PreviousFingerprint,
                item.NextFingerprint,
                item.RevisionChanged)).ToArray(),
            null);
    }

    internal LocalRepositoryManualAssignmentTransition ApplyManual(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long expectedRevision,
        LocalRepositorySessionAction action,
        string? repositoryId,
        string operationKey,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateUuid(sessionId, nameof(sessionId));
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("local_repository_assignment_transaction_mismatch");
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!LocalRepositoryCatalogValidation.IsOperationKey(operationKey))
            throw new ArgumentException("The operation key is invalid.", nameof(operationKey));
        if (action == LocalRepositorySessionAction.Assign)
            ValidateUuid(repositoryId ?? throw new ArgumentNullException(nameof(repositoryId)), nameof(repositoryId));
        else if (repositoryId is not null)
            throw new ArgumentException("The Repository target is not valid for this action.", nameof(repositoryId));
        var occurredAtText = occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (!LocalRepositoryCatalogValidation.IsCanonicalTimestamp(occurredAtText))
            throw new ArgumentOutOfRangeException(nameof(occurredAt));

        var currentState = ReadCurrentState(connection, transaction, sessionId);
        if (currentState.Revision != expectedRevision)
            throw new InvalidOperationException("local_repository_assignment_revision_stale");
        var next = action switch
        {
            LocalRepositorySessionAction.Assign => new LocalRepositoryEffectiveAssignment("assigned", "manual", repositoryId, []),
            LocalRepositorySessionAction.ExplicitlyUnassign => new LocalRepositoryEffectiveAssignment("explicitly_unassigned", "manual", null, []),
            LocalRepositorySessionAction.ResumeAutomatic => ResolveAutomatic(currentState.Candidates),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var previousFingerprint = Fingerprint(currentState.Current);
        var nextFingerprint = Fingerprint(next);
        if (string.Equals(previousFingerprint, nextFingerprint, StringComparison.Ordinal))
        {
            return new(sessionId, expectedRevision, expectedRevision, currentState.Current, currentState.Current, false);
        }

        var historyId = historyIdFactory(occurredAt);
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(historyId))
            throw new InvalidOperationException("local_repository_assignment_history_id_invalid");
        var nextRevision = checked(expectedRevision + 1);
        AdvanceRevision(connection, transaction, sessionId, expectedRevision, nextRevision, occurredAtText);
        ApplyManualOverride(connection, transaction, sessionId, action, repositoryId, nextRevision, occurredAtText);
        AppendManualHistory(
            connection,
            transaction,
            historyId,
            sessionId,
            action,
            expectedRevision,
            nextRevision,
            currentState.Current,
            next,
            previousFingerprint,
            nextFingerprint,
            operationKey,
            occurredAtText);
        return new(sessionId, expectedRevision, nextRevision, currentState.Current, next, true);
    }

    internal LocalRepositoryAssignmentSnapshot ReadCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateUuid(sessionId, nameof(sessionId));
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("local_repository_assignment_transaction_mismatch");
        var state = ReadCurrentState(connection, transaction, sessionId);
        var conflicts = state.Current.State == "conflict"
            ? Array.AsReadOnly(state.Candidates.ToArray())
            : Array.AsReadOnly(Array.Empty<string>());
        return new(
            sessionId,
            state.Revision,
            state.Current.State,
            state.Current.Authority,
            state.Current.RepositoryId,
            conflicts,
            state.UpdatedAt);
    }

    private static CurrentAssignmentState ReadCurrentState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        if (!SessionExists(connection, transaction, sessionId))
            throw new InvalidOperationException("local_repository_assignment_session_not_found");
        var candidates = ReadExistingCandidates(connection, transaction, sessionId);
        if (candidates.Length > MaximumCandidateCount)
            throw new InvalidOperationException("local_repository_assignment_cardinality_exceeded");
        var storedRevision = ReadStoredRevision(connection, transaction, sessionId);
        var revision = storedRevision ?? 0;
        var manual = ReadManualOverride(connection, transaction, sessionId);
        var current = manual is null
            ? ResolveAutomatic(candidates)
            : ReadManualChainHead(connection, transaction, sessionId, revision, manual);
        var fingerprint = Fingerprint(current);
        var resolution = new LocalRepositoryPreparedAssignmentResolution(
            sessionId,
            revision,
            current,
            current,
            Array.AsReadOnly(candidates),
            fingerprint,
            fingerprint,
            manual);
        ValidateCurrentAssignmentChain(connection, transaction, resolution);
        DateTimeOffset? updatedAt = null;
        if (storedRevision is not null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT updated_at FROM session_repository_assignment_revisions WHERE session_id=$session_id;";
            command.Parameters.AddWithValue("$session_id", sessionId);
            var value = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!LocalRepositoryCatalogValidation.IsCanonicalTimestamp(value))
                throw new InvalidOperationException("local_repository_assignment_revision_invalid");
            updatedAt = DateTimeOffset.ParseExact(value!, "O", CultureInfo.InvariantCulture);
        }
        return new(revision, current, candidates, updatedAt);
    }

    private static bool SessionExists(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sessions WHERE session_id=$session_id);";
        command.Parameters.AddWithValue("$session_id", sessionId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void ApplyManualOverride(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        LocalRepositorySessionAction action,
        string? repositoryId,
        long revision,
        string occurredAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (action == LocalRepositorySessionAction.ResumeAutomatic)
        {
            command.CommandText = "DELETE FROM session_repository_manual_overrides WHERE session_id=$session_id;";
            command.Parameters.AddWithValue("$session_id", sessionId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("local_repository_assignment_override_stale");
            return;
        }
        command.CommandText = """
            INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at)
            VALUES($session_id,$state,$repository_id,$revision,$updated_at)
            ON CONFLICT(session_id) DO UPDATE SET
                state=excluded.state,
                repository_id=excluded.repository_id,
                revision=excluded.revision,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$state", action == LocalRepositorySessionAction.Assign ? "assigned" : "explicitly_unassigned");
        command.Parameters.AddWithValue("$repository_id", repositoryId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updated_at", occurredAt);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("local_repository_assignment_override_stale");
    }

    private static void AppendManualHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string historyId,
        string sessionId,
        LocalRepositorySessionAction action,
        long previousRevision,
        long newRevision,
        LocalRepositoryEffectiveAssignment previous,
        LocalRepositoryEffectiveAssignment current,
        string previousFingerprint,
        string currentFingerprint,
        string operationKey,
        string occurredAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_repository_assignment_history(
                history_id,session_id,action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at)
            VALUES(
                $history_id,$session_id,$action,$previous_revision,$new_revision,
                $previous_fingerprint,$new_fingerprint,$previous_state,$new_state,
                $previous_authority,$new_authority,$previous_repository_id,$new_repository_id,
                'user_operation',$operation_key,NULL,$occurred_at);
            """;
        command.Parameters.AddWithValue("$history_id", historyId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$action", action switch
        {
            LocalRepositorySessionAction.Assign => "assign",
            LocalRepositorySessionAction.ExplicitlyUnassign => "explicitly_unassign",
            LocalRepositorySessionAction.ResumeAutomatic => "resume_automatic",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        });
        command.Parameters.AddWithValue("$previous_revision", previousRevision);
        command.Parameters.AddWithValue("$new_revision", newRevision);
        command.Parameters.AddWithValue("$previous_fingerprint", previousFingerprint);
        command.Parameters.AddWithValue("$new_fingerprint", currentFingerprint);
        command.Parameters.AddWithValue("$previous_state", previous.State);
        command.Parameters.AddWithValue("$new_state", current.State);
        command.Parameters.AddWithValue("$previous_authority", previous.Authority);
        command.Parameters.AddWithValue("$new_authority", current.Authority);
        command.Parameters.AddWithValue("$previous_repository_id", previous.RepositoryId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$new_repository_id", current.RepositoryId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$operation_key", operationKey);
        command.Parameters.AddWithValue("$occurred_at", occurredAt);
        command.ExecuteNonQuery();
    }

    private static string[] ReadExistingCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT repository_id
            FROM session_repository_observation_contexts
            WHERE session_id=$session_id AND admission_state='admitted'
            ORDER BY repository_id COLLATE BINARY
            LIMIT 129;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        var candidates = new List<string>(MaximumCandidateCount);
        while (reader.Read())
        {
            if (candidates.Count == MaximumCandidateCount)
                throw new InvalidOperationException("local_repository_assignment_cardinality_exceeded");
            var repositoryId = reader.GetString(0);
            ValidateUuid(repositoryId, "persisted_repository_id");
            candidates.Add(repositoryId);
        }
        return candidates.ToArray();
    }

    private static ExpectedContext[] ReadAdmittedContextsForSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.context_id,c.context_identity_sha256,c.session_id,c.repository_id,c.locator_id,
                   l.repository_id,l.locator_id,o.source_identity_sha256,c.session_event_id,c.trace_id,c.span_id
            FROM session_repository_observation_contexts c
            JOIN session_repository_observations o ON o.observation_id=c.observation_id
            LEFT JOIN local_repository_locators l
              ON l.repository_id=c.repository_id AND l.locator_id=c.locator_id
            WHERE c.session_id=$session_id AND c.admission_state='admitted'
            ORDER BY c.context_identity_sha256 COLLATE BINARY,c.context_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        var contexts = new List<ExpectedContext>();
        while (reader.Read())
        {
            if (reader.IsDBNull(3)
                || reader.IsDBNull(4)
                || reader.IsDBNull(5)
                || reader.IsDBNull(6)
                || !string.Equals(reader.GetString(3), reader.GetString(5), StringComparison.Ordinal)
                || !string.Equals(reader.GetString(4), reader.GetString(6), StringComparison.Ordinal)
                || !HasExpectedContextIdentity(reader, contextIdentityOrdinal: 1, sourceIdentityOrdinal: 7))
            {
                throw new InvalidOperationException("local_repository_assignment_context_frontier_mismatch");
            }
            contexts.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return contexts.ToArray();
    }

    private static void ValidateDurableContextGraph(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AutomaticPreparationData preparation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.context_id,c.context_identity_sha256,c.session_id,c.repository_id,c.locator_id,
                   l.repository_id,l.locator_id,o.source_identity_sha256,c.session_event_id,c.trace_id,c.span_id
            FROM session_repository_observation_contexts c
            JOIN session_repository_observations o ON o.observation_id=c.observation_id
            LEFT JOIN local_repository_locators l
              ON l.repository_id=c.repository_id AND l.locator_id=c.locator_id
            WHERE o.raw_record_id=$raw_record_id AND c.admission_state='admitted'
            ORDER BY c.context_identity_sha256 COLLATE BINARY,c.context_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$raw_record_id", preparation.RawRecordId);
        using var reader = command.ExecuteReader();
        var actual = new List<ExpectedContext>();
        while (reader.Read())
        {
            if (reader.IsDBNull(3)
                || reader.IsDBNull(4)
                || reader.IsDBNull(5)
                || reader.IsDBNull(6)
                || !string.Equals(reader.GetString(3), reader.GetString(5), StringComparison.Ordinal)
                || !string.Equals(reader.GetString(4), reader.GetString(6), StringComparison.Ordinal)
                || !HasExpectedContextIdentity(reader, contextIdentityOrdinal: 1, sourceIdentityOrdinal: 7))
            {
                throw new InvalidOperationException("local_repository_assignment_context_frontier_mismatch");
            }
            actual.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        if (!actual.SequenceEqual(preparation.ExpectedContexts))
            throw new InvalidOperationException("local_repository_assignment_context_frontier_mismatch");
    }

    private static bool HasExpectedContextIdentity(
        SqliteDataReader reader,
        int contextIdentityOrdinal,
        int sourceIdentityOrdinal)
    {
        try
        {
            var computed = LocalRepositoryIdentityHashing.ContextIdentity(new(
                reader.GetString(sourceIdentityOrdinal),
                reader.GetString(2),
                reader.GetString(sourceIdentityOrdinal + 1),
                reader.GetString(sourceIdentityOrdinal + 2),
                reader.GetString(sourceIdentityOrdinal + 3)));
            return string.Equals(reader.GetString(contextIdentityOrdinal), computed, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static long ReadRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId) => ReadStoredRevision(connection, transaction, sessionId) ?? 0;

    private static long? ReadStoredRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM session_repository_assignment_revisions WHERE session_id=$session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void ValidateCurrentAssignmentChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryPreparedAssignmentResolution resolution)
    {
        var storedRevision = ReadStoredRevision(connection, transaction, resolution.SessionId);
        if (resolution.Revision == 0 ? storedRevision is not null : storedRevision != resolution.Revision)
            throw new InvalidOperationException("local_repository_assignment_revision_stale");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT previous_revision,new_revision,
                   previous_assignment_state_sha256,new_assignment_state_sha256,
                   previous_state,new_state,previous_authority,new_authority,
                   previous_repository_id,new_repository_id,
                   action,cause_kind,operation_key,reconciliation_fingerprint
            FROM session_repository_assignment_history
            WHERE session_id=$session_id
            ORDER BY new_revision;
            """;
        command.Parameters.AddWithValue("$session_id", resolution.SessionId);
        var history = new List<PersistedAssignmentHistoryRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                history.Add(new(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13)));
            }
        }
        long chainRevision = 0;
        var chainState = "unassigned";
        var chainAuthority = "none";
        string? chainRepositoryId = null;
        var chainFingerprint = Fingerprint(ResolveAutomatic([]));
        var automaticCandidates = new HashSet<string>(StringComparer.Ordinal);
        var automaticCandidatesKnown = true;
        foreach (var row in history)
        {
            if (row.PreviousRevision != chainRevision
                || row.NewRevision != chainRevision + 1
                || !string.Equals(row.PreviousFingerprint, chainFingerprint, StringComparison.Ordinal)
                || !string.Equals(row.PreviousState, chainState, StringComparison.Ordinal)
                || !string.Equals(row.PreviousAuthority, chainAuthority, StringComparison.Ordinal)
                || !string.Equals(row.PreviousRepositoryId, chainRepositoryId, StringComparison.Ordinal)
                || !HasValidHistoryEndpoint(row.PreviousState, row.PreviousAuthority, row.PreviousRepositoryId, row.PreviousFingerprint)
                || !HasValidHistoryEndpoint(row.NewState, row.NewAuthority, row.NewRepositoryId, row.NewFingerprint)
                || !HasValidHistoryTransition(
                    row.Action,
                    row.CauseKind,
                    row.OperationKey,
                    row.ReconciliationFingerprint,
                    row.PreviousAuthority,
                    row.PreviousFingerprint,
                    row.NewState,
                    row.NewAuthority,
                    row.NewRepositoryId,
                    row.NewFingerprint)
                || !HasExactCauseReference(connection, transaction, row))
            {
                throw new InvalidOperationException("local_repository_assignment_history_stale");
            }

            if (row.Action == "automatic_reconcile")
            {
                if (!TryReadReplayableReconciliationRawRecord(
                        connection,
                        transaction,
                        row.ReconciliationFingerprint!,
                        out var rawRecordId))
                {
                    throw new InvalidOperationException("local_repository_assignment_history_stale");
                }
                var sourceCandidates = ReadAdmittedCandidatesForRawRecord(
                    connection,
                    transaction,
                    resolution.SessionId,
                    rawRecordId);
                if (sourceCandidates.Length == 0)
                    throw new InvalidOperationException("local_repository_assignment_history_stale");
                if (automaticCandidatesKnown)
                {
                    if (!MatchesAutomaticEndpoint(row.PreviousState, row.PreviousAuthority, row.PreviousRepositoryId, row.PreviousFingerprint, automaticCandidates))
                        throw new InvalidOperationException("local_repository_assignment_history_stale");
                    automaticCandidates.UnionWith(sourceCandidates);
                    if (!MatchesAutomaticEndpoint(row.NewState, row.NewAuthority, row.NewRepositoryId, row.NewFingerprint, automaticCandidates))
                        throw new InvalidOperationException("local_repository_assignment_history_stale");
                }
                else if (!HasMonotonicUnknownAutomaticTransition(row.PreviousState, row.PreviousAuthority, row.PreviousRepositoryId, row.NewState, row.NewAuthority, row.NewRepositoryId))
                {
                    throw new InvalidOperationException("local_repository_assignment_history_stale");
                }
            }
            else if (row.NewAuthority == "manual")
            {
                automaticCandidatesKnown = false;
            }
            else if (row.Action == "resume_automatic")
            {
                automaticCandidatesKnown = InitializeAutomaticCandidates(
                    row.NewState,
                    row.NewAuthority,
                    row.NewRepositoryId,
                    automaticCandidates);
            }

            chainRevision = row.NewRevision;
            chainFingerprint = row.NewFingerprint;
            chainState = row.NewState;
            chainAuthority = row.NewAuthority;
            chainRepositoryId = row.NewRepositoryId;
        }

        if (chainRevision != resolution.Revision
            || !string.Equals(chainFingerprint, resolution.PreviousFingerprint, StringComparison.Ordinal)
            || !string.Equals(chainState, resolution.Previous.State, StringComparison.Ordinal)
            || !string.Equals(chainAuthority, resolution.Previous.Authority, StringComparison.Ordinal)
            || !string.Equals(chainRepositoryId, resolution.Previous.RepositoryId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("local_repository_assignment_history_stale");
        }
    }

    private static LocalRepositoryManualOverrideSnapshot? ReadManualOverride(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state,repository_id,revision FROM session_repository_manual_overrides WHERE session_id=$session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var manual = new LocalRepositoryManualOverrideSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2));
        if (reader.Read())
            throw new InvalidOperationException("local_repository_assignment_override_conflict");
        return manual;
    }

    private static LocalRepositoryEffectiveAssignment ReadManualChainHead(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long revision,
        LocalRepositoryManualOverrideSnapshot manual)
    {
        if (revision < 1 || manual.Revision != revision)
            throw new InvalidOperationException("local_repository_assignment_revision_invalid");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT new_state,new_authority,new_repository_id,new_assignment_state_sha256
            FROM session_repository_assignment_history
            WHERE session_id=$session_id AND new_revision=$revision;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("local_repository_assignment_revision_invalid");
        var state = reader.GetString(0);
        var authority = reader.GetString(1);
        var repositoryId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var fingerprint = reader.GetString(3);
        if (reader.Read()
            || authority != "manual"
            || state != manual.State
            || !string.Equals(repositoryId, manual.RepositoryId, StringComparison.Ordinal)
            || !HasExpectedManualFingerprint(state, authority, repositoryId, fingerprint))
        {
            throw new InvalidOperationException("local_repository_assignment_revision_invalid");
        }
        return new(state, authority, repositoryId, []);
    }

    private static LocalRepositoryEffectiveAssignment ResolveAutomatic(IReadOnlyList<string> candidates) => candidates.Count switch
    {
        0 => new("unassigned", "none", null, candidates),
        1 => new("assigned", "automatic", candidates[0], candidates),
        _ => new("conflict", "automatic", null, candidates),
    };

    private static string Fingerprint(LocalRepositoryEffectiveAssignment assignment) =>
        LocalRepositoryIdentityHashing.AssignmentStateFingerprint(new(
            assignment.State,
            assignment.Authority,
            assignment.RepositoryId,
            assignment.CandidateRepositoryIds));

    private static bool HasExpectedManualFingerprint(
        string state,
        string authority,
        string? repositoryId,
        string fingerprint)
    {
        if (!string.Equals(authority, "manual", StringComparison.Ordinal))
            return true;
        if (!LocalRepositoryCatalogValidation.IsLowerSha256(fingerprint))
            return false;
        try
        {
            var expected = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(new(
                state,
                authority,
                repositoryId,
                []));
            return string.Equals(fingerprint, expected, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasValidHistoryEndpoint(
        string state,
        string authority,
        string? repositoryId,
        string fingerprint)
    {
        if (!LocalRepositoryCatalogValidation.IsLowerSha256(fingerprint))
            return false;
        var validState = state switch
        {
            "assigned" => authority is "automatic" or "manual"
                && repositoryId is not null
                && LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId),
            "unassigned" => authority == "none" && repositoryId is null,
            "explicitly_unassigned" => authority == "manual" && repositoryId is null,
            "conflict" => authority == "automatic" && repositoryId is null,
            _ => false,
        };
        if (!validState)
            return false;
        if (authority == "manual")
            return HasExpectedManualFingerprint(state, authority, repositoryId, fingerprint);
        if (state == "unassigned")
            return string.Equals(fingerprint, Fingerprint(ResolveAutomatic([])), StringComparison.Ordinal);
        if (state == "assigned")
            return string.Equals(fingerprint, Fingerprint(ResolveAutomatic([repositoryId!])), StringComparison.Ordinal);
        return true;
    }

    private static bool HasValidHistoryTransition(
        string action,
        string causeKind,
        string? operationKey,
        string? reconciliationFingerprint,
        string previousAuthority,
        string previousFingerprint,
        string newState,
        string newAuthority,
        string? newRepositoryId,
        string newFingerprint)
    {
        if (string.Equals(previousFingerprint, newFingerprint, StringComparison.Ordinal))
            return false;

        return action switch
        {
            "assign" => HasUserOperationCause(causeKind, operationKey, reconciliationFingerprint)
                && newState == "assigned"
                && newAuthority == "manual"
                && newRepositoryId is not null,
            "explicitly_unassign" => HasUserOperationCause(causeKind, operationKey, reconciliationFingerprint)
                && newState == "explicitly_unassigned"
                && newAuthority == "manual"
                && newRepositoryId is null,
            "resume_automatic" => HasUserOperationCause(causeKind, operationKey, reconciliationFingerprint)
                && previousAuthority == "manual"
                && newAuthority != "manual",
            "automatic_reconcile" => HasSourceReconciliationCause(causeKind, operationKey, reconciliationFingerprint)
                && previousAuthority != "manual"
                && newAuthority != "manual",
            _ => false,
        };
    }

    private static bool HasUserOperationCause(string causeKind, string? operationKey, string? reconciliationFingerprint) =>
        causeKind == "user_operation"
        && operationKey is not null
        && LocalRepositoryCatalogValidation.IsOperationKey(operationKey)
        && reconciliationFingerprint is null;

    private static bool HasSourceReconciliationCause(string causeKind, string? operationKey, string? reconciliationFingerprint) =>
        causeKind == "source_reconciliation"
        && operationKey is null
        && reconciliationFingerprint is not null
        && LocalRepositoryCatalogValidation.IsLowerSha256(reconciliationFingerprint);

    private static bool HasExactCauseReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedAssignmentHistoryRow row) =>
        row.CauseKind switch
        {
            "user_operation" => HasOperationReceipt(connection, transaction, row.OperationKey!),
            "source_reconciliation" => TryReadReplayableReconciliationRawRecord(
                connection,
                transaction,
                row.ReconciliationFingerprint!,
                out _),
            _ => false,
        };

    private static bool HasOperationReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM local_repository_operation_receipts WHERE operation_key=$operation_key);";
        command.Parameters.AddWithValue("$operation_key", operationKey);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool TryReadReplayableReconciliationRawRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string reconciliationFingerprint,
        out long rawRecordId)
    {
        rawRecordId = 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,state,terminal_reason
            FROM local_repository_reconciliation_queue
            WHERE reconciliation_fingerprint=$reconciliation_fingerprint;
            """;
        command.Parameters.AddWithValue("$reconciliation_fingerprint", reconciliationFingerprint);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        var candidateRawRecordId = reader.GetInt64(0);
        var evidenceKind = reader.GetString(1);
        var digest = reader.IsDBNull(2) ? null : reader.GetString(2);
        var projectorVersion = reader.GetString(3);
        var state = reader.GetString(4);
        var terminalReason = reader.IsDBNull(5) ? null : reader.GetString(5);
        if (reader.Read()
            || candidateRawRecordId < 1
            || evidenceKind != "payload_sha256"
            || !LocalRepositoryCatalogValidation.IsLowerSha256(digest)
            || projectorVersion != LocalRepositoryCatalogConstants.ProjectorVersion
            || state is not ("pending" or "leased" or "completed")
            || terminalReason is not null
            || !string.Equals(
                reconciliationFingerprint,
                LocalRepositoryIdentityHashing.ReconciliationFingerprint(
                    LocalRepositoryReconciliationEvidence.PayloadSha256(candidateRawRecordId, digest!)),
                StringComparison.Ordinal))
        {
            return false;
        }
        rawRecordId = candidateRawRecordId;
        return true;
    }

    private static string[] ReadAdmittedCandidatesForRawRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long rawRecordId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT c.repository_id
            FROM session_repository_observation_contexts c
            JOIN session_repository_observations o ON o.observation_id=c.observation_id
            WHERE c.session_id=$session_id AND c.admission_state='admitted' AND o.raw_record_id=$raw_record_id
            ORDER BY c.repository_id COLLATE BINARY
            LIMIT 129;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        using var reader = command.ExecuteReader();
        var candidates = new List<string>(MaximumCandidateCount);
        while (reader.Read())
        {
            if (candidates.Count == MaximumCandidateCount)
                throw new InvalidOperationException("local_repository_assignment_cardinality_exceeded");
            var repositoryId = reader.GetString(0);
            ValidateUuid(repositoryId, "persisted_repository_id");
            candidates.Add(repositoryId);
        }
        return candidates.ToArray();
    }

    private static bool MatchesAutomaticEndpoint(
        string state,
        string authority,
        string? repositoryId,
        string fingerprint,
        HashSet<string> candidates)
    {
        var ordered = candidates.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var expected = ResolveAutomatic(ordered);
        return state == expected.State
            && authority == expected.Authority
            && string.Equals(repositoryId, expected.RepositoryId, StringComparison.Ordinal)
            && string.Equals(fingerprint, Fingerprint(expected), StringComparison.Ordinal);
    }

    private static bool HasMonotonicUnknownAutomaticTransition(
        string previousState,
        string previousAuthority,
        string? previousRepositoryId,
        string newState,
        string newAuthority,
        string? newRepositoryId)
    {
        if (previousState == "conflict")
            return newState == "conflict" && newAuthority == "automatic" && newRepositoryId is null;
        if (previousState == "assigned" && previousAuthority == "automatic")
            return newState == "conflict" && newAuthority == "automatic" && newRepositoryId is null;
        return previousState == "unassigned"
            && previousAuthority == "none"
            && previousRepositoryId is null
            && newState is "assigned" or "conflict";
    }

    private static bool InitializeAutomaticCandidates(
        string state,
        string authority,
        string? repositoryId,
        HashSet<string> candidates)
    {
        candidates.Clear();
        if (state == "unassigned" && authority == "none" && repositoryId is null)
            return true;
        if (state == "assigned" && authority == "automatic" && repositoryId is not null)
        {
            candidates.Add(repositoryId);
            return true;
        }
        return false;
    }

    private static void AdvanceRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long previousRevision,
        long nextRevision,
        string occurredAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = previousRevision == 0
            ? """
                INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at)
                SELECT $session_id,$next_revision,$occurred_at
                WHERE NOT EXISTS(
                    SELECT 1 FROM session_repository_assignment_revisions WHERE session_id=$session_id)
                  AND NOT EXISTS(
                    SELECT 1 FROM session_repository_assignment_history WHERE session_id=$session_id);
                """
            : """
                UPDATE session_repository_assignment_revisions
                SET revision=$next_revision,updated_at=$occurred_at
                WHERE session_id=$session_id AND revision=$previous_revision;
                """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$previous_revision", previousRevision);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        command.Parameters.AddWithValue("$occurred_at", occurredAt);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("local_repository_assignment_revision_stale");
    }

    private static void AppendHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryPreparedAssignmentResolution resolution,
        long nextRevision,
        string historyId,
        string reconciliationFingerprint,
        string occurredAtText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_repository_assignment_history(
                history_id,session_id,action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at)
            VALUES(
                $history_id,$session_id,'automatic_reconcile',$previous_revision,$new_revision,
                $previous_fingerprint,$new_fingerprint,
                $previous_state,$new_state,$previous_authority,$new_authority,
                $previous_repository_id,$new_repository_id,'source_reconciliation',NULL,
                $reconciliation_fingerprint,$occurred_at);
            """;
        command.Parameters.AddWithValue("$history_id", historyId);
        command.Parameters.AddWithValue("$session_id", resolution.SessionId);
        command.Parameters.AddWithValue("$previous_revision", resolution.Revision);
        command.Parameters.AddWithValue("$new_revision", nextRevision);
        command.Parameters.AddWithValue("$previous_fingerprint", resolution.PreviousFingerprint);
        command.Parameters.AddWithValue("$new_fingerprint", resolution.NextFingerprint);
        command.Parameters.AddWithValue("$previous_state", resolution.Previous.State);
        command.Parameters.AddWithValue("$new_state", resolution.Next.State);
        command.Parameters.AddWithValue("$previous_authority", resolution.Previous.Authority);
        command.Parameters.AddWithValue("$new_authority", resolution.Next.Authority);
        command.Parameters.AddWithValue("$previous_repository_id", resolution.Previous.RepositoryId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$new_repository_id", resolution.Next.RepositoryId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$reconciliation_fingerprint", reconciliationFingerprint);
        command.Parameters.AddWithValue("$occurred_at", occurredAtText);
        command.ExecuteNonQuery();
    }

    private static void ValidateUuid(string value, string parameterName)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(value))
            throw new ArgumentException("Value must be a canonical lowercase UUIDv7.", parameterName);
    }

    internal sealed class AutomaticPreparation
    {
        private readonly AutomaticPreparationData data;

        private AutomaticPreparation(
            LocalRepositoryAssignmentReconcileStatus status,
            string? rejectedSessionId,
            AutomaticPreparationData data)
        {
            Status = status;
            RejectedSessionId = rejectedSessionId;
            this.data = data;
        }

        internal static AutomaticPreparation Create(
            LocalRepositoryAssignmentReconcileStatus status,
            string? rejectedSessionId,
            object seal,
            LocalRepositoryAssignmentResolver owner,
            SqliteConnection connection,
            SqliteTransaction transaction,
            long rawRecordId,
            IReadOnlyCollection<ExpectedContext> expectedContexts,
            IReadOnlyCollection<ExpectedSessionFrontier> expectedSessionFrontiers,
            IReadOnlyCollection<LocalRepositoryPreparedAssignmentResolution> resolutions,
            string reconciliationFingerprint,
            DateTimeOffset occurredAt,
            string occurredAtText)
        {
            if (!ReferenceEquals(seal, PreparationSeal))
                throw new InvalidOperationException("local_repository_assignment_preparation_invalid");
            return new(
                status,
                rejectedSessionId,
                new(
                    owner,
                    connection,
                    transaction,
                    rawRecordId,
                    Array.AsReadOnly(expectedContexts.ToArray()),
                    Array.AsReadOnly(expectedSessionFrontiers.ToArray()),
                    Array.AsReadOnly(resolutions.ToArray()),
                    reconciliationFingerprint,
                    occurredAt,
                    occurredAtText));
        }

        internal LocalRepositoryAssignmentReconcileStatus Status { get; }
        internal string? RejectedSessionId { get; }

        internal AutomaticPreparationData Unseal(object seal)
        {
            if (!ReferenceEquals(seal, PreparationSeal))
                throw new InvalidOperationException("local_repository_assignment_preparation_invalid");
            return data;
        }
    }

    internal sealed record AutomaticPreparationData(
        LocalRepositoryAssignmentResolver Owner,
        SqliteConnection Connection,
        SqliteTransaction Transaction,
        long RawRecordId,
        IReadOnlyList<ExpectedContext> ExpectedContexts,
        IReadOnlyList<ExpectedSessionFrontier> ExpectedSessionFrontiers,
        IReadOnlyList<LocalRepositoryPreparedAssignmentResolution> Resolutions,
        string ReconciliationFingerprint,
        DateTimeOffset OccurredAt,
        string OccurredAtText);

    internal sealed record ExpectedContext(
        string ContextId,
        string ContextIdentitySha256,
        string SessionId,
        string RepositoryId,
        string LocatorId);

    internal sealed record ExpectedSessionFrontier(
        string SessionId,
        IReadOnlyList<ExpectedContext> Contexts);

}

internal sealed record LocalRepositoryManualOverrideSnapshot(string State, string? RepositoryId, long Revision);

internal sealed record PersistedAssignmentHistoryRow(
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
    string Action,
    string CauseKind,
    string? OperationKey,
    string? ReconciliationFingerprint);

internal sealed record CurrentAssignmentState(
    long Revision,
    LocalRepositoryEffectiveAssignment Current,
    string[] Candidates,
    DateTimeOffset? UpdatedAt);

internal sealed record LocalRepositoryEffectiveAssignment(
    string State,
    string Authority,
    string? RepositoryId,
    IReadOnlyList<string> CandidateRepositoryIds);

internal sealed record LocalRepositoryPreparedAssignmentResolution(
    string SessionId,
    long Revision,
    LocalRepositoryEffectiveAssignment Previous,
    LocalRepositoryEffectiveAssignment Next,
    IReadOnlyList<string> CandidateRepositoryIds,
    string PreviousFingerprint,
    string NextFingerprint,
    LocalRepositoryManualOverrideSnapshot? ManualOverride)
{
    internal bool RevisionChanged => !string.Equals(PreviousFingerprint, NextFingerprint, StringComparison.Ordinal);
}

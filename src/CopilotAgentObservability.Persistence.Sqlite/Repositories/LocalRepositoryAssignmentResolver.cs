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
            WHERE session_id=$session_id AND admission_state='admitted';
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        var candidates = new List<string>();
        while (reader.Read())
        {
            var repositoryId = reader.GetString(0);
            ValidateUuid(repositoryId, "persisted_repository_id");
            candidates.Add(repositoryId);
        }
        return candidates.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
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
                   previous_repository_id,new_repository_id
            FROM session_repository_assignment_history
            WHERE session_id=$session_id
            ORDER BY new_revision;
            """;
        command.Parameters.AddWithValue("$session_id", resolution.SessionId);
        using var reader = command.ExecuteReader();
        long chainRevision = 0;
        var chainState = "unassigned";
        var chainAuthority = "none";
        string? chainRepositoryId = null;
        var chainFingerprint = Fingerprint(ResolveAutomatic([]));
        while (reader.Read())
        {
            var previousFingerprint = reader.GetString(2);
            var newFingerprint = reader.GetString(3);
            var previousState = reader.GetString(4);
            var newState = reader.GetString(5);
            var previousAuthority = reader.GetString(6);
            var newAuthority = reader.GetString(7);
            var previousRepositoryId = reader.IsDBNull(8) ? null : reader.GetString(8);
            var newRepositoryId = reader.IsDBNull(9) ? null : reader.GetString(9);
            if (reader.GetInt64(0) != chainRevision
                || reader.GetInt64(1) != chainRevision + 1
                || !string.Equals(previousFingerprint, chainFingerprint, StringComparison.Ordinal)
                || !string.Equals(previousState, chainState, StringComparison.Ordinal)
                || !string.Equals(previousAuthority, chainAuthority, StringComparison.Ordinal)
                || !string.Equals(previousRepositoryId, chainRepositoryId, StringComparison.Ordinal)
                || !HasExpectedManualFingerprint(previousState, previousAuthority, previousRepositoryId, previousFingerprint)
                || !HasExpectedManualFingerprint(newState, newAuthority, newRepositoryId, newFingerprint))
            {
                throw new InvalidOperationException("local_repository_assignment_history_stale");
            }

            chainRevision = reader.GetInt64(1);
            chainFingerprint = newFingerprint;
            chainState = newState;
            chainAuthority = newAuthority;
            chainRepositoryId = newRepositoryId;
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

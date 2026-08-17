using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteLocalRepositoryCatalogStore
{
    public ValueTask<ILocalRepositoryPreparedRawRecord> PrepareAsync(
        LocalRepositoryQueueLease queueLease,
        RawTelemetryRecord rawRecord,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queueLease);
        ArgumentNullException.ThrowIfNull(rawRecord);
        ArgumentNullException.ThrowIfNull(retentionLease);
        if (rawRecord.Id is not { } suppliedRawRecordId
            || suppliedRawRecordId != queueLease.RawRecordId)
        {
            throw new ArgumentException("The raw record does not match the queue lease.", nameof(rawRecord));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidInputEnvelope(queueLease, suppliedRawRecordId))
            return ValueTask.FromResult<ILocalRepositoryPreparedRawRecord>(
                new PreparedAutomaticAdmission(this, retentionLease.Grant, null, null, "catalog_schema_violation"));
        if (!string.Equals(
            SkillProjectionHashing.InputDigest(rawRecord.PayloadJson),
            queueLease.RawPayloadSha256,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException("local_repository_verified_payload_mismatch");
        }

        LocalRepositoryCaptureProvenance? provenance;
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            if (!binding.Matches(connection, transaction))
                throw new InvalidOperationException("local_repository_store_binding_mismatch");
            var provenanceResult = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
                connection,
                transaction,
                suppliedRawRecordId,
                queueLease.RawPayloadSha256!);
            provenance = provenanceResult.Status == LocalRepositoryCaptureProvenanceStatus.Valid
                ? provenanceResult.Provenance
                : null;
            transaction.Commit();
        }

        if (provenance is null)
            return ValueTask.FromResult<ILocalRepositoryPreparedRawRecord>(
                new PreparedAutomaticAdmission(this, retentionLease.Grant, null, null, "catalog_schema_violation"));

        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.BeforePayloadParsing);
        LocalRepositoryObservationParseResult? parsed = null;
        string? terminalReason = null;
        try
        {
            parsed = LocalRepositoryObservationParser.Parse(
                suppliedRawRecordId,
                rawRecord.PayloadJson,
                provenance.RawPayloadSha256,
                provenance.SourceSurface,
                provenance.SourceApplicationVersion,
                provenance.ObservedAt);
        }
        catch (JsonException)
        {
            terminalReason = "catalog_parse_failure";
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            terminalReason = "catalog_schema_violation";
        }

        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterPreparationBeforeHandoff);
        return ValueTask.FromResult<ILocalRepositoryPreparedRawRecord>(
            new PreparedAutomaticAdmission(this, retentionLease.Grant, provenance, parsed, terminalReason));
    }

    public async ValueTask ProcessAsync(
        LocalRepositoryQueueLease queueLease,
        RawTelemetryRecord rawRecord,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        CancellationToken cancellationToken)
    {
        await using var prepared = await PrepareAsync(
            queueLease,
            rawRecord,
            retentionLease,
            cancellationToken).ConfigureAwait(false);
        var handoff = queue.Heartbeat(queueLease, retentionLease);
        if (handoff.Status != LocalRepositoryQueueTransitionResult.Applied || handoff.Lease is null)
            throw new InvalidOperationException("local_repository_queue_authority_lost");
        await prepared.FinalizeAsync(handoff.Lease, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask FinalizePreparedAsync(
        LocalRepositoryQueueLease queueLease,
        RetentionReadGrant grant,
        LocalRepositoryCaptureProvenance? preparedProvenance,
        LocalRepositoryObservationParseResult? parsed,
        string? terminalReason,
        CancellationToken cancellationToken)
    {
        var suppliedRawRecordId = queueLease.RawRecordId;
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.BeforeTransaction);

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!binding.Matches(connection, transaction))
            throw new InvalidOperationException("local_repository_store_binding_mismatch");

        var processingAt = timeProvider.GetUtcNow().ToUniversalTime();

        if (terminalReason is not null || preparedProvenance is null || parsed is null)
        {
            CompleteTerminal(
                transaction,
                connection,
                queueLease,
                grant,
                suppliedRawRecordId,
                terminalReason ?? "catalog_schema_violation",
                cancellationToken);
            return ValueTask.CompletedTask;
        }

        var currentProvenance = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            suppliedRawRecordId,
            queueLease.RawPayloadSha256!);
        if (currentProvenance.Status != LocalRepositoryCaptureProvenanceStatus.Valid
            || currentProvenance.Provenance != preparedProvenance)
        {
            CompleteTerminal(transaction, connection, queueLease, grant, suppliedRawRecordId, "catalog_schema_violation", cancellationToken);
            return ValueTask.CompletedTask;
        }

        FinalizeParsed(
            connection,
            transaction,
            queueLease,
            grant,
            preparedProvenance,
            parsed,
            processingAt,
            cancellationToken);
        return ValueTask.CompletedTask;
    }

    private void FinalizeParsed(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryQueueLease queueLease,
        RetentionReadGrant grant,
        LocalRepositoryCaptureProvenance provenance,
        LocalRepositoryObservationParseResult parsed,
        DateTimeOffset processingAt,
        CancellationToken cancellationToken)
    {
        var suppliedRawRecordId = queueLease.RawRecordId;
        var join = JoinEveryContext(connection, transaction, provenance, parsed.ContextLinks);
        if (join.HasSchemaViolation)
        {
            CompleteTerminal(transaction, connection, queueLease, grant, suppliedRawRecordId, "catalog_schema_violation", cancellationToken);
            return;
        }
        if (join.HasIdentityConflict)
        {
            CompleteTerminal(transaction, connection, queueLease, grant, suppliedRawRecordId, "catalog_session_identity_conflict", cancellationToken);
            return;
        }
        if (join.HasMissingSession)
        {
            CompleteQueueOnly(transaction, connection, queueLease, grant, suppliedRawRecordId, cancellationToken, waitForSession: true);
            return;
        }

        if (join.Links.Count == 0)
        {
            checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.BeforePublication);
            CompleteQueueOnly(transaction, connection, queueLease, grant, suppliedRawRecordId, cancellationToken, waitForSession: false);
            return;
        }

        AdmissionPlan plan;
        try
        {
            plan = BuildPlan(connection, transaction, join.Links, processingAt);
        }
        catch (CatalogIdentityConflictException)
        {
            CompleteTerminal(transaction, connection, queueLease, grant, suppliedRawRecordId, "catalog_identity_conflict", cancellationToken);
            return;
        }
        catch (Exception exception) when (IsPersistedScalarReadFailure(exception))
        {
            CompleteTerminal(transaction, connection, queueLease, grant, suppliedRawRecordId, "catalog_schema_violation", cancellationToken);
            return;
        }

        LocalRepositoryAssignmentResolver.AutomaticPreparation preparation;
        try
        {
            preparation = assignmentResolver.PrepareAutomatic(
                connection,
                transaction,
                suppliedRawRecordId,
                plan.AffectedSessionIds,
                plan.ProspectiveAdmittedContexts,
                queueLease.ReconciliationFingerprint,
                processingAt);
        }
        catch (Exception exception) when (IsAssignmentCatalogContradiction(exception))
        {
            CompleteTerminal(transaction, connection, queueLease, grant, suppliedRawRecordId, "catalog_schema_violation", cancellationToken);
            return;
        }
        if (preparation.Status == LocalRepositoryAssignmentReconcileStatus.CardinalityExceeded)
        {
            CompleteTerminal(transaction, connection, queueLease, grant, suppliedRawRecordId, "catalog_cardinality_exceeded", cancellationToken);
            return;
        }

        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.BeforePublication);

        InsertRepositories(connection, transaction, plan.NewOwners);
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterRepositories);
        InsertLocators(connection, transaction, plan.NewOwners);
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterLocators);
        InsertLocatorHeads(connection, transaction, plan.NewOwners);
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterLocatorHeads);
        InsertRepositoryHistory(connection, transaction, plan.NewOwners);
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterRepositoryHistory);
        InsertObservations(connection, transaction, plan.NewObservations);
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterObservations);
        InsertContexts(connection, transaction, plan.NewContexts);
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterContexts);

        var assignment = assignmentResolver.ApplyAutomatic(connection, transaction, preparation);
        if (assignment.Status != LocalRepositoryAssignmentReconcileStatus.Applied)
            throw new InvalidOperationException("local_repository_assignment_publication_rejected");
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterAssignments);
        CompleteQueueOnly(
            transaction,
            connection,
            queueLease,
            grant,
            suppliedRawRecordId,
            cancellationToken,
            waitForSession: false);
    }

    private sealed class PreparedAutomaticAdmission(
        SqliteLocalRepositoryCatalogStore owner,
        RetentionReadGrant grant,
        LocalRepositoryCaptureProvenance? provenance,
        LocalRepositoryObservationParseResult? parsed,
        string? terminalReason) : ILocalRepositoryPreparedRawRecord
    {
        public ValueTask FinalizeAsync(LocalRepositoryQueueLease queueLease, CancellationToken cancellationToken) =>
            owner.FinalizePreparedAsync(queueLease, grant, provenance, parsed, terminalReason, cancellationToken);
    }

    private static bool IsValidInputEnvelope(
        LocalRepositoryQueueLease lease,
        long rawRecordId)
    {
        if (lease.RawRecordId != rawRecordId
            || lease.ProjectorVersion != LocalRepositoryCatalogConstants.ProjectorVersion
            || !LocalRepositoryCatalogValidation.IsLowerSha256(lease.RawPayloadSha256)
            || !LocalRepositoryCatalogValidation.IsLowerSha256(lease.ReconciliationFingerprint))
        {
            return false;
        }

        return LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, lease.RawPayloadSha256!))
            == lease.ReconciliationFingerprint;
    }

    private static JoinedContexts JoinEveryContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryCaptureProvenance provenance,
        IReadOnlyList<LocalRepositoryObservationContextLink> contextLinks)
    {
        var joined = new List<JoinedContext>(contextLinks.Count);
        var hasSchemaViolation = false;
        var hasIdentityConflict = false;
        var hasMissingSession = false;
        foreach (var link in contextLinks)
        {
            var result = LocalRepositorySessionEventJoin.ResolveContext(
                connection,
                transaction,
                provenance,
                link.TraceId ?? string.Empty,
                link.SpanId ?? string.Empty);
            switch (result.Status)
            {
                case LocalRepositorySessionEventJoinStatus.Matched
                    when result.SessionEventId is not null && result.SessionId is not null:
                    joined.Add(new(link, result.SessionEventId, result.SessionId));
                    break;
                case LocalRepositorySessionEventJoinStatus.WaitingSession:
                    hasMissingSession = true;
                    break;
                case LocalRepositorySessionEventJoinStatus.CatalogSessionIdentityConflict:
                    hasIdentityConflict = true;
                    break;
                default:
                    hasSchemaViolation = true;
                    break;
            }
        }

        return new(joined, hasSchemaViolation, hasIdentityConflict, hasMissingSession);
    }

    private AdmissionPlan BuildPlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<JoinedContext> joined,
        DateTimeOffset processingAt)
    {
        var generatedIds = new HashSet<string>(StringComparer.Ordinal);
        string Generate()
        {
            var id = NextId(processingAt);
            if (!generatedIds.Add(id))
                throw new LocalRepositoryAdmissionRetryableException("local_repository_catalog_generated_id_duplicate");
            return id;
        }

        var owners = new Dictionary<string, RepositoryOwner>(StringComparer.Ordinal);
        var newOwners = new List<NewRepositoryOwner>();
        foreach (var group in joined
            .Where(static item => item.Link.AdmissionState == LocalRepositoryAdmissionState.Admitted)
            .OrderBy(static item => item, JoinedContextComparer.Instance)
            .GroupBy(static item => item.Link.Occurrence.Locator!.LocatorSha256, StringComparer.Ordinal))
        {
            var selected = group.First();
            var locator = selected.Link.Occurrence.Locator!;
            var existing = ReadExistingOwner(connection, transaction, locator);
            if (existing is not null)
            {
                owners.Add(group.Key, existing);
                continue;
            }

            var created = new NewRepositoryOwner(
                Generate(),
                Generate(),
                Generate(),
                locator,
                selected,
                processingAt);
            newOwners.Add(created);
            owners.Add(group.Key, created.Owner);
        }

        var observations = new Dictionary<string, ObservationPlan>(StringComparer.Ordinal);
        var newObservations = new List<ObservationPlan>();
        foreach (var occurrence in joined
            .Select(static item => item.Link.Occurrence)
            .GroupBy(static item => item.SourceIdentitySha256, StringComparer.Ordinal)
            .Select(static group => RequireSingleOccurrenceMeaning(group))
            .OrderBy(static occurrence => occurrence.SourceIdentitySha256, StringComparer.Ordinal))
        {
            var existingId = ReadExistingObservation(connection, transaction, occurrence);
            var plan = new ObservationPlan(existingId ?? Generate(), occurrence);
            observations.Add(occurrence.SourceIdentitySha256, plan);
            if (existingId is null)
                newObservations.Add(plan);
        }

        var contexts = new Dictionary<string, ContextPlan>(StringComparer.Ordinal);
        var contextPairs = new Dictionary<string, ContextPlan>(StringComparer.Ordinal);
        var newContexts = new List<ContextPlan>();
        foreach (var item in joined.OrderBy(static item => item, JoinedContextComparer.Instance))
        {
            var occurrence = item.Link.Occurrence;
            var observation = observations[occurrence.SourceIdentitySha256];
            var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
                occurrence.SourceIdentitySha256,
                item.SessionId,
                item.SessionEventId,
                item.Link.TraceId!,
                item.Link.SpanId!));
            var owner = item.Link.AdmissionState == LocalRepositoryAdmissionState.Admitted
                ? owners[occurrence.Locator!.LocatorSha256]
                : null;
            var expected = new ContextPlan(
                string.Empty,
                observation.ObservationId,
                contextIdentity,
                item.SessionEventId,
                item.SessionId,
                item.Link.TraceId!,
                item.Link.SpanId!,
                AdmissionState(item.Link.AdmissionState),
                owner?.RepositoryId,
                owner?.LocatorId,
                occurrence.ObservedAt);
            if (contexts.TryGetValue(contextIdentity, out var batchIdentity))
            {
                if (!batchIdentity.SameSemantics(expected))
                    throw new CatalogIdentityConflictException();
                continue;
            }
            var pairKey = $"{observation.ObservationId}\0{item.SessionEventId}";
            if (contextPairs.TryGetValue(pairKey, out var batchPair)
                && !string.Equals(batchPair.ContextIdentitySha256, contextIdentity, StringComparison.Ordinal))
            {
                throw new CatalogIdentityConflictException();
            }

            var existingId = ReadExistingContext(connection, transaction, expected);
            var plan = expected with { ContextId = existingId ?? Generate() };
            contexts.Add(contextIdentity, plan);
            contextPairs.Add(pairKey, plan);
            if (existingId is null)
                newContexts.Add(plan);
        }

        foreach (var owner in newOwners)
        {
            var selected = contexts.Values.Single(context =>
                context.ContextIdentitySha256 == ContextIdentity(owner.SelectedContext));
            owner.ContextIdentitySha256 = selected.ContextIdentitySha256;
        }

        var prospective = contexts.Values
            .Where(static context => context.AdmissionState == "admitted")
            .Select(static context => new LocalRepositoryProspectiveAssignmentContext(
                context.ContextId,
                context.ContextIdentitySha256,
                context.SessionId,
                context.RepositoryId!,
                context.LocatorId!))
            .ToArray();
        EnsureGeneratedIdsAreAvailable(connection, transaction, generatedIds);
        return new(
            newOwners,
            newObservations,
            newContexts,
            joined.Select(static item => item.SessionId).Distinct(StringComparer.Ordinal).ToArray(),
            prospective);
    }

    private static LocalRepositoryPhysicalOccurrence RequireSingleOccurrenceMeaning(
        IGrouping<string, LocalRepositoryPhysicalOccurrence> group)
    {
        var first = group.First();
        if (group.Skip(1).Any(item => !SameOccurrence(first, item)))
            throw new CatalogIdentityConflictException();
        return first;
    }

    private static bool SameOccurrence(
        LocalRepositoryPhysicalOccurrence left,
        LocalRepositoryPhysicalOccurrence right) =>
        left.RawRecordId == right.RawRecordId
        && left.ResourceSpanOrdinal == right.ResourceSpanOrdinal
        && left.ScopeSpanOrdinal == right.ScopeSpanOrdinal
        && left.SpanOrdinal == right.SpanOrdinal
        && left.ScopeKind == right.ScopeKind
        && left.AttributeOrdinal == right.AttributeOrdinal
        && left.AttributeKey == right.AttributeKey
        && left.RawPayloadSha256 == right.RawPayloadSha256
        && left.SourceSurface == right.SourceSurface
        && left.SourceApplicationVersion == right.SourceApplicationVersion
        && left.ObservedAt == right.ObservedAt
        && left.Classification == right.Classification
        && SameLocator(left.Locator, right.Locator);

    private static bool SameLocator(GitHubRepositoryLocator? left, GitHubRepositoryLocator? right) =>
        left is null && right is null
        || left is not null && right is not null
        && left.CanonicalLocator == right.CanonicalLocator
        && left.LocatorSha256 == right.LocatorSha256
        && left.DisplayOwner == right.DisplayOwner
        && left.DisplayRepository == right.DisplayRepository;

    private static RepositoryOwner? ReadExistingOwner(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GitHubRepositoryLocator locator)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT l.locator_id,l.repository_id,l.kind,l.canonical_locator,l.locator_sha256
            FROM local_repository_locators AS l
            WHERE l.kind='github_repository' AND l.locator_sha256=$locator_sha256
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$locator_sha256", locator.LocatorSha256);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var owner = new RepositoryOwner(reader.GetString(1), reader.GetString(0));
        if (reader.GetString(2) != "github_repository"
            || reader.GetString(3) != locator.CanonicalLocator
            || reader.GetString(4) != locator.LocatorSha256
            || reader.Read())
        {
            throw new CatalogIdentityConflictException();
        }
        return owner;
    }

    private static string? ReadExistingObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryPhysicalOccurrence occurrence)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT observation_id,raw_record_id,raw_payload_sha256,resource_span_ordinal,
                   scope_span_ordinal,span_ordinal,attribute_ordinal,scope_kind,attribute_key,
                   value_classification,locator_kind,canonical_locator,locator_sha256,
                   display_owner,display_repository,source_surface,source_application_version,observed_at
            FROM session_repository_observations
            WHERE source_identity_sha256=$source_identity_sha256
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$source_identity_sha256", occurrence.SourceIdentitySha256);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var observationId = reader.GetString(0);
        if (reader.GetInt64(1) != occurrence.RawRecordId
            || reader.GetString(2) != occurrence.RawPayloadSha256
            || reader.GetInt32(3) != occurrence.ResourceSpanOrdinal
            || NullableInt32(reader, 4) != occurrence.ScopeSpanOrdinal
            || NullableInt32(reader, 5) != occurrence.SpanOrdinal
            || reader.GetInt32(6) != occurrence.AttributeOrdinal
            || reader.GetString(7) != ScopeKind(occurrence.ScopeKind)
            || reader.GetString(8) != occurrence.AttributeKey
            || reader.GetString(9) != Classification(occurrence.Classification)
            || NullableText(reader, 10) != (occurrence.Locator is null ? null : "github_repository")
            || NullableText(reader, 11) != occurrence.Locator?.CanonicalLocator
            || NullableText(reader, 12) != occurrence.Locator?.LocatorSha256
            || NullableText(reader, 13) != occurrence.Locator?.DisplayOwner
            || NullableText(reader, 14) != occurrence.Locator?.DisplayRepository
            || reader.GetString(15) != occurrence.SourceSurface
            || NullableText(reader, 16) != occurrence.SourceApplicationVersion
            || reader.GetString(17) != Timestamp(occurrence.ObservedAt)
            || reader.Read())
        {
            throw new CatalogIdentityConflictException();
        }
        return observationId;
    }

    private static string? ReadExistingContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContextPlan expected)
    {
        using var identity = connection.CreateCommand();
        identity.Transaction = transaction;
        identity.CommandText = """
            SELECT context_id,observation_id,session_event_id,session_id,trace_id,span_id,
                   admission_state,repository_id,locator_id,observed_at
            FROM session_repository_observation_contexts
            WHERE context_identity_sha256=$context_identity_sha256
            LIMIT 2;
            """;
        identity.Parameters.AddWithValue("$context_identity_sha256", expected.ContextIdentitySha256);
        using (var reader = identity.ExecuteReader())
        {
            if (reader.Read())
            {
                var contextId = reader.GetString(0);
                if (reader.GetString(1) != expected.ObservationId
                    || reader.GetString(2) != expected.SessionEventId
                    || reader.GetString(3) != expected.SessionId
                    || reader.GetString(4) != expected.TraceId
                    || reader.GetString(5) != expected.SpanId
                    || reader.GetString(6) != expected.AdmissionState
                    || NullableText(reader, 7) != expected.RepositoryId
                    || NullableText(reader, 8) != expected.LocatorId
                    || reader.GetString(9) != Timestamp(expected.ObservedAt)
                    || reader.Read())
                {
                    throw new CatalogIdentityConflictException();
                }
                return contextId;
            }
        }

        using var pair = connection.CreateCommand();
        pair.Transaction = transaction;
        pair.CommandText = """
            SELECT context_identity_sha256
            FROM session_repository_observation_contexts
            WHERE observation_id=$observation_id AND session_event_id=$session_event_id
            LIMIT 1;
            """;
        pair.Parameters.AddWithValue("$observation_id", expected.ObservationId);
        pair.Parameters.AddWithValue("$session_event_id", expected.SessionEventId);
        if (pair.ExecuteScalar() is string otherIdentity
            && otherIdentity != expected.ContextIdentitySha256)
        {
            throw new CatalogIdentityConflictException();
        }
        return null;
    }

    private static void EnsureGeneratedIdsAreAvailable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> generatedIds)
    {
        foreach (var id in generatedIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT 1 FROM (
                    SELECT repository_id AS id FROM local_repositories
                    UNION ALL SELECT locator_id FROM local_repository_locators
                    UNION ALL SELECT observation_id FROM session_repository_observations
                    UNION ALL SELECT context_id FROM session_repository_observation_contexts
                    UNION ALL SELECT history_id FROM local_repository_history
                    UNION ALL SELECT history_id FROM session_repository_assignment_history
                    UNION ALL SELECT session_id FROM sessions
                    UNION ALL SELECT event_id FROM session_events
                    UNION ALL SELECT queue_id FROM local_repository_reconciliation_queue)
                WHERE id=$id LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is not null)
                throw new LocalRepositoryAdmissionRetryableException("local_repository_catalog_generated_id_unavailable");
        }
    }

    private DateTimeOffset ValidatePublicationAuthority(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        long rawRecordId,
        RetentionGrantPublicationSet publications,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publicationAt = timeProvider.GetUtcNow().ToUniversalTime();
        if (!RetentionCatalogStore.ValidateLocalRepositoryOperationLease(
            connection,
            transaction,
            grant,
            rawRecordId,
            publications.ScopeFor(0, grant),
            publicationAt))
        {
            throw new InvalidOperationException("local_repository_retention_authority_lost");
        }
        return publicationAt;
    }

    private DateTimeOffset ValidateFinalizationAuthority(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        long rawRecordId,
        RetentionGrantPublicationSet publications,
        CancellationToken cancellationToken)
    {
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.BeforeQueueCompletion);
        return ValidatePublicationAuthority(
            connection,
            transaction,
            grant,
            rawRecordId,
            publications,
            cancellationToken);
    }

    private void CompleteTerminal(
        SqliteTransaction transaction,
        SqliteConnection connection,
        LocalRepositoryQueueLease lease,
        RetentionReadGrant grant,
        long rawRecordId,
        string reason,
        CancellationToken cancellationToken)
    {
        using var publications = RetentionGrantPublicationSet.EnterInOrder(
            [new RetentionGrantPublicationMember(grant, 0)]);
        var at = ValidateFinalizationAuthority(
            connection,
            transaction,
            grant,
            rawRecordId,
            publications,
            cancellationToken);
        RequireApplied(queue.TryFailTerminal(connection, transaction, lease, at, reason));
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.BeforePublicationClaim);
        if (!publications.TryClaimCommittedHandles(out var publicationClaim))
            throw new InvalidOperationException("local_repository_retention_authority_lost");
        using (publicationClaim)
        {
            checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterPublicationClaimBeforeCommit);
            transaction.Commit();
            checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterCommitBeforePublicationClaimRelease);
        }
    }

    private void CompleteQueueOnly(
        SqliteTransaction transaction,
        SqliteConnection connection,
        LocalRepositoryQueueLease lease,
        RetentionReadGrant grant,
        long rawRecordId,
        CancellationToken cancellationToken,
        bool waitForSession)
    {
        using var publications = RetentionGrantPublicationSet.EnterInOrder(
            [new RetentionGrantPublicationMember(grant, 0)]);
        var at = ValidateFinalizationAuthority(
            connection,
            transaction,
            grant,
            rawRecordId,
            publications,
            cancellationToken);
        var result = waitForSession
            ? queue.TryWaitForSession(connection, transaction, lease, at)
            : queue.TryComplete(connection, transaction, lease, at);
        RequireApplied(result);
        checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.BeforePublicationClaim);
        if (!publications.TryClaimCommittedHandles(out var publicationClaim))
            throw new InvalidOperationException("local_repository_retention_authority_lost");
        using (publicationClaim)
        {
            checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterPublicationClaimBeforeCommit);
            transaction.Commit();
            checkpoint?.Reached(LocalRepositoryAdmissionCheckpoint.AfterCommitBeforePublicationClaimRelease);
        }
    }

    private static void RequireApplied(LocalRepositoryQueueTransitionResult result)
    {
        if (result != LocalRepositoryQueueTransitionResult.Applied)
            throw new InvalidOperationException("local_repository_queue_authority_lost");
    }

    private static void InsertRepositories(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<NewRepositoryOwner> owners)
    {
        foreach (var owner in owners)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at)
                VALUES($repository_id,$display_name,1,$at,$at);
                """;
            command.Parameters.AddWithValue("$repository_id", owner.Owner.RepositoryId);
            command.Parameters.AddWithValue("$display_name", owner.Locator.DisplayRepository);
            command.Parameters.AddWithValue("$at", Timestamp(owner.ProcessingAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertLocators(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<NewRepositoryOwner> owners)
    {
        foreach (var owner in owners)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_repository_locators(
                    locator_id,repository_id,kind,canonical_locator,locator_sha256,source,
                    display_owner,display_repository,created_at)
                VALUES(
                    $locator_id,$repository_id,'github_repository',$canonical_locator,
                    $locator_sha256,'observed',$display_owner,$display_repository,$at);
                """;
            command.Parameters.AddWithValue("$locator_id", owner.Owner.LocatorId);
            command.Parameters.AddWithValue("$repository_id", owner.Owner.RepositoryId);
            command.Parameters.AddWithValue("$canonical_locator", owner.Locator.CanonicalLocator);
            command.Parameters.AddWithValue("$locator_sha256", owner.Locator.LocatorSha256);
            command.Parameters.AddWithValue("$display_owner", owner.Locator.DisplayOwner);
            command.Parameters.AddWithValue("$display_repository", owner.Locator.DisplayRepository);
            command.Parameters.AddWithValue("$at", Timestamp(owner.ProcessingAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertLocatorHeads(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<NewRepositoryOwner> owners)
    {
        foreach (var owner in owners)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_repository_locator_heads(repository_id,kind,locator_id,updated_at)
                VALUES($repository_id,'github_repository',$locator_id,$at);
                """;
            command.Parameters.AddWithValue("$repository_id", owner.Owner.RepositoryId);
            command.Parameters.AddWithValue("$locator_id", owner.Owner.LocatorId);
            command.Parameters.AddWithValue("$at", Timestamp(owner.ProcessingAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertRepositoryHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<NewRepositoryOwner> owners)
    {
        foreach (var owner in owners)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_repository_history(
                    history_id,repository_id,action,previous_revision,new_revision,locator_id,
                    cause_kind,operation_key,context_identity_sha256,occurred_at)
                VALUES(
                    $history_id,$repository_id,'create_observed',0,1,$locator_id,
                    'source_context',NULL,$context_identity_sha256,$at);
                """;
            command.Parameters.AddWithValue("$history_id", owner.HistoryId);
            command.Parameters.AddWithValue("$repository_id", owner.Owner.RepositoryId);
            command.Parameters.AddWithValue("$locator_id", owner.Owner.LocatorId);
            command.Parameters.AddWithValue("$context_identity_sha256", owner.ContextIdentitySha256);
            command.Parameters.AddWithValue("$at", Timestamp(owner.ProcessingAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertObservations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<ObservationPlan> observations)
    {
        foreach (var observation in observations)
        {
            var occurrence = observation.Occurrence;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO session_repository_observations(
                    observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,
                    resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,
                    scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,
                    locator_sha256,display_owner,display_repository,source_surface,
                    source_application_version,observed_at)
                VALUES(
                    $observation_id,$source_identity_sha256,$raw_record_id,$raw_payload_sha256,
                    $resource_span_ordinal,$scope_span_ordinal,$span_ordinal,$attribute_ordinal,
                    $scope_kind,$attribute_key,$value_classification,$locator_kind,$canonical_locator,
                    $locator_sha256,$display_owner,$display_repository,$source_surface,
                    $source_application_version,$observed_at);
                """;
            command.Parameters.AddWithValue("$observation_id", observation.ObservationId);
            command.Parameters.AddWithValue("$source_identity_sha256", occurrence.SourceIdentitySha256);
            command.Parameters.AddWithValue("$raw_record_id", occurrence.RawRecordId);
            command.Parameters.AddWithValue("$raw_payload_sha256", occurrence.RawPayloadSha256);
            command.Parameters.AddWithValue("$resource_span_ordinal", occurrence.ResourceSpanOrdinal);
            command.Parameters.AddWithValue("$scope_span_ordinal", (object?)occurrence.ScopeSpanOrdinal ?? DBNull.Value);
            command.Parameters.AddWithValue("$span_ordinal", (object?)occurrence.SpanOrdinal ?? DBNull.Value);
            command.Parameters.AddWithValue("$attribute_ordinal", occurrence.AttributeOrdinal);
            command.Parameters.AddWithValue("$scope_kind", ScopeKind(occurrence.ScopeKind));
            command.Parameters.AddWithValue("$attribute_key", occurrence.AttributeKey);
            command.Parameters.AddWithValue("$value_classification", Classification(occurrence.Classification));
            command.Parameters.AddWithValue("$locator_kind", occurrence.Locator is null ? DBNull.Value : "github_repository");
            command.Parameters.AddWithValue("$canonical_locator", (object?)occurrence.Locator?.CanonicalLocator ?? DBNull.Value);
            command.Parameters.AddWithValue("$locator_sha256", (object?)occurrence.Locator?.LocatorSha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$display_owner", (object?)occurrence.Locator?.DisplayOwner ?? DBNull.Value);
            command.Parameters.AddWithValue("$display_repository", (object?)occurrence.Locator?.DisplayRepository ?? DBNull.Value);
            command.Parameters.AddWithValue("$source_surface", occurrence.SourceSurface);
            command.Parameters.AddWithValue("$source_application_version", (object?)occurrence.SourceApplicationVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$observed_at", Timestamp(occurrence.ObservedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertContexts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<ContextPlan> contexts)
    {
        foreach (var context in contexts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO session_repository_observation_contexts(
                    context_id,observation_id,context_identity_sha256,session_event_id,session_id,
                    trace_id,span_id,admission_state,repository_id,locator_id,observed_at)
                VALUES(
                    $context_id,$observation_id,$context_identity_sha256,$session_event_id,$session_id,
                    $trace_id,$span_id,$admission_state,$repository_id,$locator_id,$observed_at);
                """;
            command.Parameters.AddWithValue("$context_id", context.ContextId);
            command.Parameters.AddWithValue("$observation_id", context.ObservationId);
            command.Parameters.AddWithValue("$context_identity_sha256", context.ContextIdentitySha256);
            command.Parameters.AddWithValue("$session_event_id", context.SessionEventId);
            command.Parameters.AddWithValue("$session_id", context.SessionId);
            command.Parameters.AddWithValue("$trace_id", context.TraceId);
            command.Parameters.AddWithValue("$span_id", context.SpanId);
            command.Parameters.AddWithValue("$admission_state", context.AdmissionState);
            command.Parameters.AddWithValue("$repository_id", (object?)context.RepositoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("$locator_id", (object?)context.LocatorId ?? DBNull.Value);
            command.Parameters.AddWithValue("$observed_at", Timestamp(context.ObservedAt));
            command.ExecuteNonQuery();
        }
    }

    private static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string? NullableText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string ScopeKind(LocalRepositoryObservationScopeKind value) => value switch
    {
        LocalRepositoryObservationScopeKind.Resource => "resource",
        LocalRepositoryObservationScopeKind.Span => "span",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Classification(LocalRepositoryOccurrenceClassification value) => value switch
    {
        LocalRepositoryOccurrenceClassification.Admitted => "admitted",
        LocalRepositoryOccurrenceClassification.InvalidLocator => "invalid_locator",
        LocalRepositoryOccurrenceClassification.InvalidType => "invalid_type",
        LocalRepositoryOccurrenceClassification.DuplicateKey => "duplicate_key",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string AdmissionState(LocalRepositoryAdmissionState value) => value switch
    {
        LocalRepositoryAdmissionState.Admitted => "admitted",
        LocalRepositoryAdmissionState.Shadowed => "shadowed",
        LocalRepositoryAdmissionState.InvalidLocator => "invalid_locator",
        LocalRepositoryAdmissionState.InvalidType => "invalid_type",
        LocalRepositoryAdmissionState.DuplicateKey => "duplicate_key",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ContextIdentity(JoinedContext joined) =>
        LocalRepositoryIdentityHashing.ContextIdentity(new(
            joined.Link.Occurrence.SourceIdentitySha256,
            joined.SessionId,
            joined.SessionEventId,
            joined.Link.TraceId!,
            joined.Link.SpanId!));

    private static bool IsPersistedScalarReadFailure(Exception exception) =>
        exception is InvalidCastException or FormatException or OverflowException;

    private static bool IsAssignmentCatalogContradiction(Exception exception) =>
        IsPersistedScalarReadFailure(exception)
        || exception is InvalidOperationException
        {
            Message: "local_repository_assignment_context_frontier_mismatch"
                or "local_repository_assignment_override_conflict"
                or "local_repository_assignment_revision_invalid"
        };

    private sealed record JoinedContext(
        LocalRepositoryObservationContextLink Link,
        string SessionEventId,
        string SessionId);

    private sealed record JoinedContexts(
        IReadOnlyList<JoinedContext> Links,
        bool HasSchemaViolation,
        bool HasIdentityConflict,
        bool HasMissingSession);

    private sealed record RepositoryOwner(string RepositoryId, string LocatorId);

    private sealed class NewRepositoryOwner(
        string repositoryId,
        string locatorId,
        string historyId,
        GitHubRepositoryLocator locator,
        JoinedContext selectedContext,
        DateTimeOffset processingAt)
    {
        internal RepositoryOwner Owner { get; } = new(repositoryId, locatorId);
        internal string HistoryId { get; } = historyId;
        internal GitHubRepositoryLocator Locator { get; } = locator;
        internal JoinedContext SelectedContext { get; } = selectedContext;
        internal DateTimeOffset ProcessingAt { get; } = processingAt;
        internal string ContextIdentitySha256 { get; set; } = string.Empty;
    }

    private sealed record ObservationPlan(
        string ObservationId,
        LocalRepositoryPhysicalOccurrence Occurrence);

    private sealed record ContextPlan(
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
        DateTimeOffset ObservedAt)
    {
        internal bool SameSemantics(ContextPlan other) =>
            ObservationId == other.ObservationId
            && ContextIdentitySha256 == other.ContextIdentitySha256
            && SessionEventId == other.SessionEventId
            && SessionId == other.SessionId
            && TraceId == other.TraceId
            && SpanId == other.SpanId
            && AdmissionState == other.AdmissionState
            && RepositoryId == other.RepositoryId
            && LocatorId == other.LocatorId
            && ObservedAt == other.ObservedAt;
    }

    private sealed record AdmissionPlan(
        IReadOnlyList<NewRepositoryOwner> NewOwners,
        IReadOnlyList<ObservationPlan> NewObservations,
        IReadOnlyList<ContextPlan> NewContexts,
        IReadOnlyCollection<string> AffectedSessionIds,
        IReadOnlyCollection<LocalRepositoryProspectiveAssignmentContext> ProspectiveAdmittedContexts);

    private sealed class JoinedContextComparer : IComparer<JoinedContext>
    {
        internal static JoinedContextComparer Instance { get; } = new();

        public int Compare(JoinedContext? left, JoinedContext? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = left.Link.Occurrence.ResourceSpanOrdinal.CompareTo(right.Link.Occurrence.ResourceSpanOrdinal);
            if (result != 0) return result;
            result = left.Link.ContextScopeSpanOrdinal.CompareTo(right.Link.ContextScopeSpanOrdinal);
            if (result != 0) return result;
            result = left.Link.ContextSpanOrdinal.CompareTo(right.Link.ContextSpanOrdinal);
            if (result != 0) return result;
            result = left.Link.Occurrence.ScopeKind.CompareTo(right.Link.Occurrence.ScopeKind);
            if (result != 0) return result;
            result = left.Link.Occurrence.AttributeOrdinal.CompareTo(right.Link.Occurrence.AttributeOrdinal);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Link.Occurrence.AttributeKey, right.Link.Occurrence.AttributeKey);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.SessionId, right.SessionId);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.SessionEventId, right.SessionEventId);
        }
    }

    private sealed class CatalogIdentityConflictException : Exception;
}

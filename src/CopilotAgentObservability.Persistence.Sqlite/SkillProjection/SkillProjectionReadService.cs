using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record SkillProjectionInvocationClaim(
    long GenerationId,
    string SourceArm,
    long RawRecordId,
    string TraceId,
    string SpanId,
    int SpanOrdinal,
    string SkillName,
    string? SkillSource,
    string? InvocationTrigger,
    string SourceApplicationVersion);

internal sealed record SkillProjectionInventoryClaim(
    long GenerationId,
    string SourceArm,
    long RawRecordId,
    string TraceId,
    string? SessionId,
    int ObservedNameCount,
    IReadOnlyList<string> RetainedNames,
    bool NamesTruncated,
    string SourceApplicationVersion);

internal sealed record SkillProjectionCurrentSearchFact(
    string SessionId,
    string SourceIdentity,
    string SkillName,
    string? ExpiresAt = null);

internal sealed record SkillProjectionCanonicalInvocation(
    string CanonicalIdentity,
    string SessionId,
    string? ProducerTraceId,
    string? ProducerSpanId,
    string? OtelSourceIdentity,
    string? OtelSkillName,
    string? SdkSourceIdentity,
    string? SdkSkillName,
    string? SdkExpiresAt,
    string? ExecutionSourceKind = null,
    string? ExecutionSourceIdentity = null,
    string? OtelCarrierEventId = null,
    string? SdkCarrierEventId = null,
    string? SdkSourceParentEventId = null,
    string? SdkSourceAdapter = null)
{
    internal IEnumerable<SkillProjectionCurrentSearchFact> ProjectSearchFacts()
    {
        if (OtelSourceIdentity is not null && OtelSkillName is not null)
            yield return new(SessionId, OtelSourceIdentity, OtelSkillName);
        if (SdkSourceIdentity is not null && SdkSkillName is not null)
            yield return new(SessionId, SdkSourceIdentity, SdkSkillName, SdkExpiresAt);
    }
}

internal sealed record SkillProjectionCurrentInvocationProjection(
    string State,
    IReadOnlyList<SkillProjectionCanonicalInvocation> Invocations)
{
    internal int? InvocationCount => State == "current" ? Invocations.Count : null;

    internal IReadOnlyList<SkillProjectionCurrentSearchFact> SearchFacts =>
        State == "current" ? Invocations.SelectMany(static invocation => invocation.ProjectSearchFacts()).ToArray() : [];
}

internal sealed class SkillProjectionReadService
{
    private readonly string databasePath;
    private readonly ISkillRegistryGenerationAuthority? registryAuthority;

    internal SkillProjectionReadService(
        string databasePath,
        ISkillRegistryGenerationAuthority? registryAuthority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.registryAuthority = registryAuthority;
    }

    internal IReadOnlyList<SkillProjectionInvocationClaim> ListCurrentInvocations(string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT invocation.generation_id,invocation.source_arm,invocation.raw_record_id,
                   invocation.trace_id,invocation.span_id,invocation.span_ordinal,
                   invocation.skill_name,invocation.skill_source,
                   invocation.invocation_trigger,invocation.source_application_version
            FROM skill_projection_invocations AS invocation
            JOIN skill_projection_generations AS generation
              ON generation.generation_id=invocation.generation_id
            JOIN skill_projection_trace_heads AS head
              ON head.trace_id=invocation.trace_id
             AND head.current_generation_id=invocation.generation_id
            JOIN source_trace_compatibility_revisions AS revision
              ON revision.trace_id=invocation.trace_id
             AND revision.current_revision=generation.compatibility_revision
            WHERE invocation.trace_id=$trace_id
              AND invocation.source_arm='otel_trace_span'
              AND generation.lifecycle='current'
              AND NOT EXISTS(
                    SELECT 1
                    FROM skill_projection_generation_inputs AS input
                    WHERE input.generation_id=generation.generation_id
                      AND input.input_evidence_kind='deleted_before_digest_v10'
              )
              AND revision.current_effective_state='resolved'
              AND revision.current_exact_version=invocation.source_application_version
            ORDER BY invocation.invocation_id;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        SqliteCommandExecutionObserver.Executing();
        using var reader = command.ExecuteReader();
        var rows = new List<SkillProjectionInvocationClaim>();
        while (reader.Read())
        {
            rows.Add(new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9)));
        }
        return rows;
    }

    internal IReadOnlyList<SkillProjectionInventoryClaim> ListCurrentInventories(
        string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT inventory.inventory_id,inventory.generation_id,inventory.source_arm,
                   inventory.raw_record_id,inventory.trace_id,inventory.session_id,
                   inventory.observed_name_count,inventory.retained_name_count,
                   inventory.names_truncated,inventory.source_application_version,
                   name.name_ordinal,name.skill_name
            FROM skill_projection_inventories AS inventory
            JOIN skill_projection_generations AS generation
              ON generation.generation_id=inventory.generation_id
            JOIN skill_projection_trace_heads AS head
              ON head.trace_id=inventory.trace_id
             AND head.current_generation_id=inventory.generation_id
            JOIN source_trace_compatibility_revisions AS revision
              ON revision.trace_id=inventory.trace_id
             AND revision.current_revision=generation.compatibility_revision
            LEFT JOIN skill_projection_inventory_names AS name
              ON name.inventory_id=inventory.inventory_id
            WHERE inventory.trace_id=$trace_id
              AND inventory.source_arm='otel_trace_span'
              AND generation.lifecycle='current'
              AND NOT EXISTS(
                    SELECT 1
                    FROM skill_projection_generation_inputs AS input
                    WHERE input.generation_id=generation.generation_id
                      AND input.input_evidence_kind='deleted_before_digest_v10'
              )
              AND revision.current_effective_state='resolved'
              AND revision.current_exact_version=inventory.source_application_version
            ORDER BY inventory.inventory_id,name.name_ordinal;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        SqliteCommandExecutionObserver.Executing();
        using var reader = command.ExecuteReader();
        var order = new List<long>();
        var accumulators = new Dictionary<long, InventoryAccumulator>();
        while (reader.Read())
        {
            var inventoryId = reader.GetInt64(0);
            if (!accumulators.TryGetValue(inventoryId, out var accumulator))
            {
                accumulator = new(
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt64(8) != 0,
                    reader.GetString(9));
                accumulators.Add(inventoryId, accumulator);
                order.Add(inventoryId);
            }
            if (!reader.IsDBNull(10))
            {
                if (reader.GetInt32(10) != accumulator.Names.Count)
                    throw new InvalidOperationException("skill_projection_inventory_invalid");
                accumulator.Names.Add(reader.GetString(11));
            }
        }
        return order
            .Select(inventoryId =>
            {
                var accumulator = accumulators[inventoryId];
                if (accumulator.Names.Count != accumulator.RetainedNameCount)
                    throw new InvalidOperationException("skill_projection_inventory_invalid");
                return new SkillProjectionInventoryClaim(
                    accumulator.GenerationId,
                    accumulator.SourceArm,
                    accumulator.RawRecordId,
                    accumulator.TraceId,
                    accumulator.SessionId,
                    accumulator.ObservedNameCount,
                    accumulator.Names.ToArray(),
                    accumulator.NamesTruncated,
                    accumulator.SourceApplicationVersion);
            })
            .ToArray();
    }

    // #154 current authorization: proves the complete available snapshot/claim equality and the
    // exact producer tuple under one SQLite transaction, then captures the registry generation,
    // acquires a non-mutating read lease (with one pre-lease recapture), and re-proves
    // capture/lease identity and exact acceptance before handing back an opaque capability.
    internal SkillProjectionCurrentSdkClaimAuthorizationResult TryAcquireCurrentSdkClaimAuthorization(
        Guid sessionId,
        Guid snapshotId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (registryAuthority is null)
            return SkillProjectionCurrentSdkClaimAuthorizationResult.Unavailable;

        var claim = ProveCurrentSdkClaim(sessionId, snapshotId, timeProvider);
        return claim.Outcome switch
        {
            SkillProjectionSdkClaimProofOutcome.Busy => SkillProjectionCurrentSdkClaimAuthorizationResult.Busy,
            SkillProjectionSdkClaimProofOutcome.Proved => AcquireGenerationAuthorization(
                claim.Tuple!, claim.SkillName!, claim.SkillSource),
            _ => SkillProjectionCurrentSdkClaimAuthorizationResult.Unavailable
        };
    }

    // The metadata route's point-in-time diagnostic. It shares the claim proof above so there is
    // one authority for "which exact producer tuple does this snapshot's claim carry", but it takes
    // no registry generation lease: metadata reports a point observation, while only current-file
    // holds a capability across later work.
    internal SkillProjectionSdkClaimProofResult ProveCurrentSdkClaim(
        Guid sessionId,
        Guid snapshotId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Pooling = false,
                    Mode = SqliteOpenMode.ReadWrite
                }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);

            var metadataResult = SkillInvocationSnapshotMetadataReader.ReadInTransaction(
                connection,
                transaction,
                sessionId,
                snapshotId,
                timeProvider);

            if (metadataResult.Outcome == SkillInvocationSnapshotMetadataOutcome.Busy)
                return SkillProjectionSdkClaimProofResult.Busy;

            // #154 has no not-found outcome; a snapshot that already passed the current-file
            // lookup and historical-state arm cannot legitimately disappear or turn unreadable
            // here, so every non-available shape is a graph contradiction.
            if (metadataResult.Outcome != SkillInvocationSnapshotMetadataOutcome.Found ||
                metadataResult.Facts is null ||
                !metadataResult.Facts.IsAvailable ||
                metadataResult.Facts.ClaimId is null)
                return SkillProjectionSdkClaimProofResult.Unavailable;

            var facts = metadataResult.Facts;

            var claim = ReadSdkClaimRow(connection, transaction, sessionId, facts.ClaimId.Value.ToString("D"));
            if (claim is null || !ClaimMatchesSnapshot(claim, sessionId, facts))
                return SkillProjectionSdkClaimProofResult.Unavailable;

            var proved = SkillProjectionSdkClaimProofResult.ForProved(
                new SkillRegistryProducerTuple(
                    claim.SourceApplicationVersion,
                    claim.AdapterVersion,
                    claim.NormalizationVersion,
                    claim.PayloadSchema,
                    claim.SchemaFingerprint),
                claim.SkillName,
                claim.SkillSource);

            transaction.Commit();
            return proved;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return SkillProjectionSdkClaimProofResult.Busy;
        }
    }

    private SkillProjectionCurrentSdkClaimAuthorizationResult AcquireGenerationAuthorization(
        SkillRegistryProducerTuple tuple,
        string skillName,
        string? skillSource) =>
        AcquireGenerationAuthorization(registryAuthority, tuple, skillName, skillSource);

    private static SkillProjectionCurrentSdkClaimAuthorizationResult AcquireGenerationAuthorization(
        ISkillRegistryGenerationAuthority? registryAuthority,
        SkillRegistryProducerTuple tuple,
        string skillName,
        string? skillSource)
    {
        if (registryAuthority is null)
            return SkillProjectionCurrentSdkClaimAuthorizationResult.Unavailable;

        ISkillRegistryGenerationCapture? capture = null;
        ISkillRegistryGenerationLease? lease = null;

        // One pre-lease recapture is permitted: the second attempt is the last, so a second
        // pre-lease churn or a lease-acquisition failure lands in sanitized unavailability.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            capture = registryAuthority.CaptureGeneration();
            if (capture is null)
                return SkillProjectionCurrentSdkClaimAuthorizationResult.Unavailable;

            if (registryAuthority.TryAcquireGenerationReadLease(capture, out lease))
                break;

            lease = null;
        }

        if (capture is null || lease is null)
            return SkillProjectionCurrentSdkClaimAuthorizationResult.Unavailable;

        try
        {
            if (!registryAuthority.VerifyGenerationIdentity(capture, lease))
                return SkillProjectionCurrentSdkClaimAuthorizationResult.Unavailable;

            // Within a mechanically valid generation a revoked tuple (with a valid accepted
            // predecessor) or an absent tuple never yields a capability.
            if (!registryAuthority.IsProducerTupleAccepted(lease, tuple))
                return SkillProjectionCurrentSdkClaimAuthorizationResult.NotCurrent;

            var authorization = new SkillProjectionCurrentSdkClaimAuthorization(skillName, skillSource, lease);
            lease = null;
            return SkillProjectionCurrentSdkClaimAuthorizationResult.ForAcquired(authorization);
        }
        finally
        {
            // Disposes the lease unless ownership was transferred to the capability above.
            lease?.Dispose();
        }
    }

    private sealed record SdkClaimRow(
        Guid SessionId,
        Guid EventId,
        string SourceApplicationVersion,
        string AdapterVersion,
        string NormalizationVersion,
        string PayloadSchema,
        string SchemaFingerprint,
        string PayloadSha256,
        string SourceEventId,
        string SourceAdapter,
        string SourceSurface,
        string? ProducerTraceId,
        string? ProducerSpanId,
        string SkillName,
        string? SkillSource,
        string? InvocationTrigger,
        string CreatedAtText);

    private static SdkClaimRow? ReadSdkClaimRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string claimId)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT session_id, event_id, source_application_version, adapter_version,
                       normalization_version, payload_schema, schema_fingerprint,
                       payload_sha256,source_event_id,source_adapter,source_surface,
                       producer_trace_id,producer_span_id,skill_name,skill_source,invocation_trigger,created_at
                FROM skill_projection_sdk_claims
                WHERE claim_id = @claimId AND session_id = @sessionId
                LIMIT 2;
                """;
            command.Parameters.Add(new SqliteParameter("@claimId", claimId));
            command.Parameters.Add(new SqliteParameter("@sessionId", sessionId.ToString("D")));

            SqliteCommandExecutionObserver.Executing();
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var sessionIdText = reader.GetString(0);
            if (!Guid.TryParseExact(sessionIdText, "D", out var claimSessionId) || claimSessionId != sessionId)
                return null;
            if (!Guid.TryParseExact(reader.GetString(1), "D", out var eventId))
                return null;

            var row = new SdkClaimRow(
                claimSessionId,
                eventId,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.GetString(16));

            // The metadata graph already proved ClaimCount(...) == 1; a second row is a
            // contradiction.
            if (reader.Read())
                return null;

            return row;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ClaimMatchesSnapshot(
        SdkClaimRow claim,
        Guid sessionId,
        SkillInvocationSnapshotMetadataFacts facts)
    {
        if (claim.SessionId != sessionId)
            return false;

        var eventIdText = facts.EventId.ToString("D");
        if (!string.Equals(eventIdText, claim.EventId.ToString("D"), StringComparison.Ordinal))
            return false;

        return string.Equals(claim.SourceApplicationVersion, facts.SourceApplicationVersion, StringComparison.Ordinal) &&
            string.Equals(claim.AdapterVersion, facts.AdapterVersion, StringComparison.Ordinal) &&
            string.Equals(claim.NormalizationVersion, facts.NormalizationVersion, StringComparison.Ordinal) &&
            string.Equals(claim.PayloadSchema, facts.PayloadSchema, StringComparison.Ordinal) &&
            string.Equals(claim.SchemaFingerprint, facts.SchemaFingerprint, StringComparison.Ordinal) &&
            string.Equals(claim.PayloadSha256, facts.PayloadSha256, StringComparison.Ordinal) &&
            string.Equals(claim.SourceEventId, facts.SourceEventId, StringComparison.Ordinal) &&
            string.Equals(claim.SourceAdapter, facts.SourceAdapter, StringComparison.Ordinal) &&
            string.Equals(claim.SourceSurface, facts.SourceSurface, StringComparison.Ordinal) &&
            NullableTextEquals(claim.ProducerTraceId, facts.TraceId) &&
            NullableTextEquals(claim.ProducerSpanId, facts.SpanId) &&
            string.Equals(claim.SkillName, facts.Name, StringComparison.Ordinal) &&
            NullableTextEquals(claim.SkillSource, facts.Source) &&
            NullableTextEquals(claim.InvocationTrigger, facts.Trigger) &&
            string.Equals(claim.CreatedAtText, facts.CapturedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool NullableTextEquals(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && string.Equals(left, right, StringComparison.Ordinal);

    internal static IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> ReadCurrentInvocationProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset acceptedAt,
        ISkillRegistryGenerationAuthority? registryAuthority)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (sessionIds.Count == 0 || !ComponentInstalled(connection, transaction))
            return new Dictionary<string, SkillProjectionCurrentInvocationProjection>(StringComparer.Ordinal);

        var sdkAuthorityInstalled = SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, transaction);
        if (registryAuthority is null && sdkAuthorityInstalled)
            return sessionIds.Distinct(StringComparer.Ordinal).ToDictionary(
                static sessionId => sessionId,
                static _ => new SkillProjectionCurrentInvocationProjection("unavailable", []),
                StringComparer.Ordinal);
        var otel = ReadCurrentOtelInvocationFacts(connection, transaction, sessionIds);
        IReadOnlySet<string> unavailableSdkSessions = new HashSet<string>(StringComparer.Ordinal);
        var sdk = registryAuthority is null || !sdkAuthorityInstalled
            ? []
            : ReadCurrentSdkInvocationFacts(connection, transaction, sessionIds, registryAuthority,
                new FixedProjectionTimeProvider(acceptedAt), out unavailableSdkSessions);
        var result = new Dictionary<string, SkillProjectionCurrentInvocationProjection>(StringComparer.Ordinal);
        var otelBySession = otel.GroupBy(static row => row.Fact.SessionId, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var sdkBySession = sdk.GroupBy(static row => row.Fact.SessionId, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        foreach (var sessionId in sessionIds.Distinct(StringComparer.Ordinal))
        {
            if (unavailableSdkSessions.Contains(sessionId))
            {
                result[sessionId] = new("unavailable", []);
                continue;
            }
            var sessionOtel = otelBySession.GetValueOrDefault(sessionId, []);
            var sessionSdk = sdkBySession.GetValueOrDefault(sessionId, []);
            sessionOtel = DeduplicateExactProducerPairs(sessionOtel);
            sessionSdk = DeduplicateExactProducerPairs(sessionSdk);
            if (sessionOtel.Length == 0 && sessionSdk.Length == 0) continue;

            if (sessionOtel.Length == 0)
            {
                result[sessionId] = new("current", sessionSdk.Select(ToSdkOnlyCanonical).ToArray());
                continue;
            }
            if (sessionSdk.Length == 0)
            {
                result[sessionId] = new("current", sessionOtel.Select(ToOtelOnlyCanonical).ToArray());
                continue;
            }

            var remaining = sessionOtel.GroupBy(static row => (row.TraceId, row.SpanId))
                .ToDictionary(static group => group.Key, static group => new Queue<InvocationFact>(group));
            var admitted = new List<SkillProjectionCanonicalInvocation>();
            var matched = 0;
            foreach (var sdkRow in sessionSdk)
            {
                if (sdkRow.TraceId is null || sdkRow.SpanId is null ||
                    !remaining.TryGetValue((sdkRow.TraceId, sdkRow.SpanId), out var candidates) || candidates.Count == 0)
                    continue;
                var otelRow = candidates.Dequeue();
                admitted.Add(ToExactPairCanonical(otelRow, sdkRow));
                matched++;
            }
            result[sessionId] = matched == sessionOtel.Length && matched == sessionSdk.Length
                ? new("current", admitted)
                : new("certification_pending", []);
        }
        return result;
    }

    private sealed record InvocationFact(
        SkillProjectionCurrentSearchFact Fact,
        string? TraceId,
        string? SpanId,
        string? ExecutionSourceIdentity,
        string? CarrierEventId,
        string? SourceParentEventId,
        string? SourceAdapter);

    private static SkillProjectionCanonicalInvocation ToOtelOnlyCanonical(InvocationFact row) => new(
        row.TraceId is not null && row.SpanId is not null
            ? "producer:" + row.TraceId + ":" + row.SpanId
            : row.Fact.SourceIdentity,
        row.Fact.SessionId,
        row.TraceId,
        row.SpanId,
        row.Fact.SourceIdentity,
        row.Fact.SkillName,
        null,
        null,
        null,
        row.ExecutionSourceIdentity is null ? null : "session_run",
        row.ExecutionSourceIdentity,
        row.CarrierEventId,
        null,
        null,
        null);

    private static SkillProjectionCanonicalInvocation ToSdkOnlyCanonical(InvocationFact row) => new(
        row.Fact.SourceIdentity,
        row.Fact.SessionId,
        row.TraceId,
        row.SpanId,
        null,
        null,
        row.Fact.SourceIdentity,
        row.Fact.SkillName,
        row.Fact.ExpiresAt,
        row.ExecutionSourceIdentity is null ? null : "session_run",
        row.ExecutionSourceIdentity,
        null,
        row.CarrierEventId,
        row.SourceParentEventId,
        row.SourceAdapter);

    private static SkillProjectionCanonicalInvocation ToExactPairCanonical(InvocationFact otel, InvocationFact sdk) => new(
        "producer:" + otel.TraceId + ":" + otel.SpanId,
        otel.Fact.SessionId,
        otel.TraceId,
        otel.SpanId,
        otel.Fact.SourceIdentity,
        otel.Fact.SkillName,
        sdk.Fact.SourceIdentity,
        sdk.Fact.SkillName,
        sdk.Fact.ExpiresAt,
        otel.ExecutionSourceIdentity is not null && string.Equals(otel.ExecutionSourceIdentity, sdk.ExecutionSourceIdentity, StringComparison.Ordinal) ? "session_run" : null,
        otel.ExecutionSourceIdentity is not null && string.Equals(otel.ExecutionSourceIdentity, sdk.ExecutionSourceIdentity, StringComparison.Ordinal) ? otel.ExecutionSourceIdentity : null,
        otel.CarrierEventId,
        sdk.CarrierEventId,
        sdk.SourceParentEventId,
        sdk.SourceAdapter);

    private static InvocationFact[] DeduplicateExactProducerPairs(IEnumerable<InvocationFact> facts) =>
        facts.GroupBy(static row =>
                !string.IsNullOrEmpty(row.TraceId) && !string.IsNullOrEmpty(row.SpanId)
                    ? "producer:" + row.TraceId + "\0" + row.SpanId
                    : "claim:" + row.Fact.SourceIdentity,
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    private sealed class FixedProjectionTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static IReadOnlyList<InvocationFact> ReadCurrentOtelInvocationFacts(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds)
    {
        var hasSessionEvents = TableInstalled(connection, transaction, "session_events");
        var carrierCte = hasSessionEvents
            ? """
              exact_carriers AS (
                SELECT e.session_id,e.trace_id,substr(e.source_event_id,instr(e.source_event_id,'/')+1) span_id,
                       min(e.run_id) run_id,min(e.event_id) event_id,count(*) carrier_count
                FROM session_events e
                WHERE e.source_adapter='otel-exact' AND e.run_id IS NOT NULL AND e.trace_id IS NOT NULL
                  AND e.source_event_id=e.trace_id||'/'||substr(e.source_event_id,instr(e.source_event_id,'/')+1)
                GROUP BY e.session_id,e.trace_id,span_id),
              admitted_carriers AS (SELECT * FROM exact_carriers WHERE carrier_count=1),
              """
            : "";
        var carrierColumns = hasSessionEvents ? "c.run_id,c.event_id" : "NULL,NULL";
        var carrierJoin = hasSessionEvents
            ? "LEFT JOIN admitted_carriers c ON c.session_id=invocation.session_id AND c.trace_id=invocation.trace_id AND c.span_id=invocation.span_id"
            : "";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH {carrierCte}
            admitted_invocations AS (SELECT 1 marker)
            SELECT invocation.session_id,CAST(invocation.raw_record_id AS TEXT)||':'||CAST(invocation.span_ordinal AS TEXT),invocation.skill_name,
                   invocation.trace_id,invocation.span_id,{carrierColumns}
            FROM skill_projection_invocations invocation
            JOIN skill_projection_generations generation ON generation.generation_id=invocation.generation_id AND generation.lifecycle='current'
            JOIN skill_projection_trace_heads head ON head.trace_id=invocation.trace_id AND head.current_generation_id=invocation.generation_id
            JOIN source_trace_compatibility_revisions revision ON revision.trace_id=invocation.trace_id AND revision.current_revision=generation.compatibility_revision
              AND revision.current_effective_state='resolved' AND revision.current_exact_version=invocation.source_application_version
            {carrierJoin}
            WHERE invocation.source_arm='otel_trace_span' AND invocation.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND NOT EXISTS(SELECT 1 FROM skill_projection_generation_inputs input WHERE input.generation_id=generation.generation_id AND input.input_evidence_kind='deleted_before_digest_v10')
            ORDER BY invocation.session_id COLLATE BINARY,invocation.invocation_id;
            """;
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(sessionIds));
        SqliteCommandExecutionObserver.Executing();
        using var reader = command.ExecuteReader();
        var result = new List<InvocationFact>();
        while (reader.Read()) result.Add(new(new(reader.GetString(0), "otel:" + reader.GetString(1), reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), null, null));
        return result;
    }

    private static IReadOnlyList<InvocationFact> ReadCurrentSdkInvocationFacts(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds,
        ISkillRegistryGenerationAuthority registryAuthority, TimeProvider timeProvider,
        out IReadOnlySet<string> unavailableSessions)
    {
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        unavailableSessions = unavailable;
        if (!SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, transaction)) return [];
        var candidates = ReadStructurallyValidSdkCandidates(connection, transaction, sessionIds, timeProvider);
        var result = new List<InvocationFact>();
        var authorizations = new Dictionary<SkillRegistryProducerTuple, SkillProjectionCurrentSdkClaimAuthorizationResult>();
        try
        {
            foreach (var candidate in candidates)
            {
                var tuple = new SkillRegistryProducerTuple(candidate.SourceApplicationVersion, candidate.AdapterVersion,
                    candidate.NormalizationVersion, candidate.PayloadSchema, candidate.SchemaFingerprint);
                if (!authorizations.TryGetValue(tuple, out var authorizationResult))
                {
                    authorizationResult = AcquireGenerationAuthorization(registryAuthority, tuple, candidate.SkillName, candidate.SkillSource);
                    authorizations.Add(tuple, authorizationResult);
                }
                if (authorizationResult.Outcome is SkillRegistryCurrentAuthorizationOutcome.Busy or SkillRegistryCurrentAuthorizationOutcome.Unavailable)
                    unavailable.Add(candidate.SessionId);
                if (authorizationResult.Authorization is null) continue;
                result.Add(new(new(candidate.SessionId, "sdk:" + candidate.ClaimId, candidate.SkillName, candidate.ExpiresAt),
                    candidate.ProducerTraceId, candidate.ProducerSpanId, candidate.RunId, candidate.EventId, candidate.SourceParentEventId, candidate.SourceAdapter));
            }
        }
        finally
        {
            foreach (var authorization in authorizations.Values) authorization.Authorization?.Dispose();
        }
        return result;
    }

    internal static IReadOnlyList<SkillProjectionCurrentSearchFact> ReadStructurallyValidSdkFactsForBackupValidation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        TimeProvider timeProvider)
    {
        if (sessionIds.Count == 0 || !ComponentInstalled(connection, transaction)
            || !SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, transaction)) return [];

        return ReadStructurallyValidSdkCandidates(connection, transaction, sessionIds, timeProvider)
            .Select(static candidate => new SkillProjectionCurrentSearchFact(candidate.SessionId, candidate.ClaimId,
                candidate.SkillName, candidate.ExpiresAt)).ToArray();
    }

    private sealed record StructurallyValidSdkCandidate(string SessionId, string ClaimId, string EventId, string? RunId,
        string? SourceParentEventId, string SourceApplicationVersion, string AdapterVersion, string NormalizationVersion,
        string PayloadSchema, string SchemaFingerprint, string SkillName, string? SkillSource,
        string? ProducerTraceId, string? ProducerSpanId, string? ExpiresAt, string ReceiptFingerprint,
        string SourceAdapter, string SourceEventId, string SourceSurface, string NativeSessionId, string? RunNativeId,
        bool SourceEphemeral, DateTimeOffset OccurredAt, string PayloadSha256, ulong PayloadBytes, string State,
        string Reason, string? Trigger, string? BodySha256, ulong? BodyUtf8Bytes, string? DefinitionPathSha256,
        ulong? DefinitionPathUtf8Bytes, string ContentDocumentSha256)
    {
        internal bool HasValidReceiptFingerprint() => string.Equals(ReceiptFingerprint,
            SkillInvocationSnapshotReceiptFingerprint.Compute(new(SourceAdapter, SourceEventId, SourceSurface,
                NativeSessionId, RunNativeId, SourceParentEventId, SourceEphemeral, ProducerTraceId, ProducerSpanId,
                OccurredAt, SourceApplicationVersion, AdapterVersion, NormalizationVersion, PayloadSchema,
                SchemaFingerprint, PayloadSha256, PayloadBytes, State, Reason, SkillName, SkillSource, Trigger,
                BodySha256, BodyUtf8Bytes, DefinitionPathSha256, DefinitionPathUtf8Bytes, ContentDocumentSha256)),
            StringComparison.Ordinal);
    }

    private static IReadOnlyList<StructurallyValidSdkCandidate> ReadStructurallyValidSdkCandidates(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, TimeProvider timeProvider)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.session_id,s.claim_id,s.event_id,s.run_id,s.source_parent_event_id,
                   s.source_application_version,s.adapter_version,s.normalization_version,s.payload_schema,s.schema_fingerprint,
                   s.name,s.source,s.trace_id,s.span_id,CASE WHEN i.state='retained_by_policy' THEN NULL ELSE i.expires_at END,r.request_fingerprint_sha256,
                   e.source_adapter,e.source_event_id,e.source_surface,s.native_session_id,u.native_run_id,
                   s.source_ephemeral,e.occurred_at,s.payload_sha256,s.payload_bytes,s.state,s.reason,s.trigger,
                   s.body_sha256,s.body_utf8_bytes,s.definition_path_sha256,s.definition_path_utf8_bytes,s.content_document_sha256
            FROM skill_invocation_snapshots s
            JOIN session_events e ON e.session_id=s.session_id AND e.event_id=s.event_id
            JOIN skill_invocation_snapshot_receipts r ON r.snapshot_id=s.snapshot_id
            JOIN retention_items i ON i.item_id=s.content_item_id AND i.store_kind='session_event_content'
              AND i.source_item_id=s.event_id AND i.captured_at=s.captured_at
            JOIN session_event_content c ON c.event_id=s.event_id AND c.content_kind='application/json'
              AND c.captured_at=s.captured_at AND c.expires_at=i.expires_at
            JOIN sessions se ON se.session_id=s.session_id AND se.created_at<=s.captured_at AND se.updated_at>=s.captured_at
            JOIN session_native_ids n ON n.source_surface='copilot-sdk' AND n.native_session_id=s.native_session_id
              AND n.session_id=s.session_id AND n.binding_kind IN ('native','explicit_resume','explicit_handoff')
            JOIN skill_projection_sdk_claims k ON k.claim_id=s.claim_id AND k.session_id=s.session_id AND k.event_id=s.event_id
              AND k.source_application_version=s.source_application_version AND k.adapter_version=s.adapter_version
              AND k.normalization_version=s.normalization_version AND k.payload_schema=s.payload_schema
              AND k.schema_fingerprint=s.schema_fingerprint AND k.source_event_id=e.source_event_id
              AND k.source_adapter=e.source_adapter AND k.source_surface=e.source_surface AND k.payload_sha256=s.payload_sha256
              AND k.producer_trace_id IS s.trace_id AND k.producer_span_id IS s.span_id AND k.skill_name=s.name
              AND k.skill_source IS s.source AND k.invocation_trigger IS s.trigger AND k.created_at=s.captured_at
            LEFT JOIN session_runs u ON u.session_id=s.session_id AND u.run_id=s.run_id
            WHERE s.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND s.state='available' AND s.reason='none' AND s.claim_id IS NOT NULL AND s.name IS NOT NULL
              AND s.body_sha256 IS NOT NULL AND s.body_utf8_bytes IS NOT NULL
              AND s.definition_path_sha256 IS NOT NULL AND s.definition_path_utf8_bytes IS NOT NULL
              AND ((s.trace_id IS NULL AND s.span_id IS NULL) OR (s.trace_id IS NOT NULL AND s.span_id IS NOT NULL))
              AND s.created_at=s.captured_at AND e.type='skill.invoked' AND e.source_adapter='copilot-sdk-stream'
              AND e.source_surface='copilot-sdk' AND e.content_state='available'
              AND e.status IS NULL AND e.match_kind IS NULL AND e.parent_event_id IS NULL
              AND e.terminal_outcome IS NULL AND e.terminal_policy_version IS NULL
              AND e.source_application_version=s.source_application_version AND e.adapter_version=s.adapter_version
              AND e.normalization_version=s.normalization_version AND e.schema_fingerprint=s.schema_fingerprint
              AND e.trace_id IS s.trace_id AND e.run_id IS s.run_id
              AND r.source_adapter=e.source_adapter AND r.source_event_id=e.source_event_id AND r.created_at=s.captured_at
              AND i.state IN ('retained_by_policy','expiring') AND i.read_denied_at IS NULL AND i.deleted_at IS NULL
              AND i.error_code IS NULL AND (i.state='retained_by_policy' OR i.expires_at>$now)
              AND NOT EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id)
              AND (s.run_id IS NULL OR (u.source_surface='copilot-sdk' AND u.native_run_id IS NOT NULL AND length(u.native_run_id)>0
                AND (SELECT COUNT(*) FROM session_runs ux WHERE ux.session_id=s.session_id AND ux.source_surface='copilot-sdk' AND ux.native_run_id=u.native_run_id)=1))
              AND (SELECT COUNT(*) FROM skill_invocation_snapshot_receipts rx WHERE rx.snapshot_id=s.snapshot_id)=1
              AND (SELECT COUNT(*) FROM skill_projection_sdk_claims kx WHERE kx.claim_id=s.claim_id AND kx.session_id=s.session_id)=1
            ORDER BY s.session_id COLLATE BINARY,s.snapshot_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(sessionIds));
        command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        SqliteCommandExecutionObserver.Executing();
        using var reader = command.ExecuteReader();
        var result = new List<StructurallyValidSdkCandidate>();
        while (reader.Read())
        {
            var candidate = new StructurallyValidSdkCandidate(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetString(15), reader.GetString(16), reader.GetString(17), reader.GetString(18), reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20), reader.GetInt64(21) == 1,
                DateTimeOffset.Parse(reader.GetString(22), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(23), checked((ulong)reader.GetInt64(24)), reader.GetString(25), reader.GetString(26),
                reader.IsDBNull(27) ? null : reader.GetString(27), reader.IsDBNull(28) ? null : reader.GetString(28),
                reader.IsDBNull(29) ? null : checked((ulong)reader.GetInt64(29)), reader.IsDBNull(30) ? null : reader.GetString(30),
                reader.IsDBNull(31) ? null : checked((ulong)reader.GetInt64(31)), reader.GetString(32));
            if (candidate.HasValidReceiptFingerprint()) result.Add(candidate);
        }
        return result;
    }

    private static bool ComponentInstalled(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_version WHERE component='skill_projection' AND version=1);";
        SqliteCommandExecutionObserver.Executing();
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool TableInstalled(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", name);
        SqliteCommandExecutionObserver.Executing();
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private sealed class InventoryAccumulator(
        long generationId,
        string sourceArm,
        long rawRecordId,
        string traceId,
        string? sessionId,
        int observedNameCount,
        int retainedNameCount,
        bool namesTruncated,
        string sourceApplicationVersion)
    {
        internal long GenerationId { get; } = generationId;
        internal string SourceArm { get; } = sourceArm;
        internal long RawRecordId { get; } = rawRecordId;
        internal string TraceId { get; } = traceId;
        internal string? SessionId { get; } = sessionId;
        internal int ObservedNameCount { get; } = observedNameCount;
        internal int RetainedNameCount { get; } = retainedNameCount;
        internal bool NamesTruncated { get; } = namesTruncated;
        internal string SourceApplicationVersion { get; } = sourceApplicationVersion;
        internal List<string> Names { get; } = [];
    }
}

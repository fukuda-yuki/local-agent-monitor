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

internal sealed record SkillProjectionSdkClaim(
    string ClaimId,
    string SessionId,
    string EventId,
    string SourceEventId,
    string SourceAdapter,
    string SourceSurface,
    string SourceApplicationVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint,
    string PayloadSha256,
    string? ProducerTraceId,
    string? ProducerSpanId,
    string SkillName,
    string? SkillSource,
    string? InvocationTrigger);

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

internal sealed record SkillProjectionSessionInvocationAggregate(
    int? InvocationCount,
    string? State);

internal sealed class SkillProjectionReadService
{
    private readonly string databasePath;

    internal SkillProjectionReadService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
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

    internal IReadOnlyList<SkillProjectionSdkClaim> ListCurrentSdkClaims(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return [];
    }

    internal SkillProjectionSessionInvocationAggregate GetSessionInvocationAggregate(
        string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var otelPairs = ListCurrentOtelPairs(sessionId);
        var sdkClaims = ListCurrentSdkClaims(sessionId);
        if (otelPairs.Count == 0 && sdkClaims.Count == 0)
            return new(null, null);
        if (otelPairs.Count == 0)
            return new(sdkClaims.Count, "current");
        if (sdkClaims.Count == 0)
            return new(otelPairs.Count, "current");

        var remainingOtel = otelPairs
            .GroupBy(static pair => pair)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var matched = 0;
        foreach (var claim in sdkClaims)
        {
            if (claim.ProducerTraceId is null || claim.ProducerSpanId is null)
                continue;
            var pair = (claim.ProducerTraceId, claim.ProducerSpanId);
            if (!remainingOtel.TryGetValue(pair, out var available) || available == 0)
                continue;
            remainingOtel[pair] = available - 1;
            matched++;
        }
        return matched == otelPairs.Count && matched == sdkClaims.Count
            ? new(matched, "current")
            : new(null, "certification_pending");
    }

    private IReadOnlyList<(string TraceId, string SpanId)> ListCurrentOtelPairs(
        string sessionId)
    {
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
            SELECT invocation.trace_id,invocation.span_id
            FROM skill_projection_invocations AS invocation
            JOIN skill_projection_generations AS generation
              ON generation.generation_id=invocation.generation_id
            JOIN skill_projection_trace_heads AS head
              ON head.trace_id=invocation.trace_id
             AND head.current_generation_id=invocation.generation_id
            JOIN source_trace_compatibility_revisions AS revision
              ON revision.trace_id=invocation.trace_id
             AND revision.current_revision=generation.compatibility_revision
            WHERE invocation.session_id=$session_id
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
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        var rows = new List<(string TraceId, string SpanId)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
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

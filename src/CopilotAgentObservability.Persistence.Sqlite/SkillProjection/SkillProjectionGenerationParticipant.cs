using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record SkillProjectionGenerationChange(
    long CompatibilityRevision,
    long GenerationId,
    string InputFrontierSha256);

internal static class SkillProjectionGenerationParticipant
{
    internal const string CurrentProjectorVersion = "skill-projector-1";

    internal static SkillProjectionGenerationChange? AdmitOrdinaryObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        TraceSourceVersionResolutionRow? before,
        DateTimeOffset admittedAt,
        ILocalWorkspaceProjectionTransactionParticipant? workspaceParticipant = null)
    {
        var after = SourceCompatibilityReconciler.ReadEffectiveTrace(connection, transaction, traceId)
            ?? throw new InvalidOperationException("source_compatibility_trace_not_found");
        if (before is not null)
        {
            EnsureCompatibilityRevisionExists(
                connection,
                transaction,
                traceId,
                before.State,
                before.SourceApplicationVersion,
                Timestamp(admittedAt));
        }

        var currentRevision = ScalarLong(
            connection,
            transaction,
            "SELECT current_revision FROM source_trace_compatibility_revisions WHERE trace_id=$trace_id;",
            ("$trace_id", traceId)) ?? 0;
        var inputs = ReadFrontier(connection, transaction, traceId);
        var frontierDigest = SkillProjectionHashing.FrontierDigest(traceId, inputs);
        var desiredAlreadyMatches = ScalarLong(
            connection,
            transaction,
            """
            SELECT generation.generation_id
            FROM skill_projection_trace_heads AS head
            JOIN skill_projection_generations AS generation
              ON generation.generation_id=head.desired_generation_id
            WHERE head.trace_id=$trace_id
              AND generation.compatibility_revision=$revision
              AND generation.input_frontier_sha256=$frontier
              AND generation.projector_version=$projector;
            """,
            ("$trace_id", traceId),
            ("$revision", currentRevision),
            ("$frontier", frontierDigest),
            ("$projector", CurrentProjectorVersion)) is not null;
        var semanticChange = before is not null && before != after;
        if (!semanticChange && desiredAlreadyMatches)
            return null;

        return Advance(
            connection,
            transaction,
            traceId,
            after.State,
            after.SourceApplicationVersion,
            CurrentProjectorVersion,
            admittedAt,
            bumpCompatibilityRevision: semanticChange,
            workspaceParticipant);
    }

    internal static SkillProjectionGenerationChange Advance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        TraceSourceVersionResolutionState effectiveState,
        string? exactVersion,
        string projectorVersion,
        DateTimeOffset changedAt,
        bool bumpCompatibilityRevision,
        ILocalWorkspaceProjectionTransactionParticipant? workspaceParticipant = null)
    {
        var now = Timestamp(changedAt);
        var compatibilityRevision = EnsureAndReadCompatibilityRevision(
            connection,
            transaction,
            traceId,
            effectiveState,
            exactVersion,
            now,
            bumpCompatibilityRevision);
        var inputs = ReadFrontier(connection, transaction, traceId);
        var frontierDigest = SkillProjectionHashing.FrontierDigest(traceId, inputs);

        var oldDesired = ScalarLong(
            connection,
            transaction,
            "SELECT desired_generation_id FROM skill_projection_trace_heads WHERE trace_id=$trace_id;",
            ("$trace_id", traceId));
        var oldCurrent = ScalarLong(
            connection,
            transaction,
            "SELECT current_generation_id FROM skill_projection_trace_heads WHERE trace_id=$trace_id;",
            ("$trace_id", traceId));
        var invalidatedSessions = new List<string>();
        if (oldCurrent is not null)
        {
            using var sessions = connection.CreateCommand();
            sessions.Transaction = transaction;
            sessions.CommandText = "SELECT DISTINCT session_id FROM skill_projection_invocations WHERE generation_id=$generation_id AND session_id IS NOT NULL ORDER BY session_id;";
            sessions.Parameters.AddWithValue("$generation_id", oldCurrent.Value);
            using var reader = sessions.ExecuteReader();
            while (reader.Read()) invalidatedSessions.Add(reader.GetString(0));
        }
        if (oldDesired is not null)
        {
            SupersedeGeneration(connection, transaction, oldDesired.Value, now);
        }
        if (oldCurrent is not null)
        {
            SupersedeGeneration(connection, transaction, oldCurrent.Value, now);
        }

        var resolved = effectiveState == TraceSourceVersionResolutionState.Resolved;
        var inputUnavailable = inputs.Any(static input =>
            input.EvidenceKind == SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10);
        var lifecycle = inputUnavailable
            ? "input_unavailable"
            : resolved ? "pending" : "superseded";
        var generationId = InsertOrReadGeneration(
            connection,
            transaction,
            traceId,
            compatibilityRevision,
            frontierDigest,
            projectorVersion,
            lifecycle,
            now);
        InsertInputs(connection, transaction, generationId, inputs);
        InsertQueue(
            connection,
            transaction,
            generationId,
            traceId,
            compatibilityRevision,
            frontierDigest,
            projectorVersion,
            lifecycle,
            inputUnavailable ? "skill_projection_input_unavailable" : null);
        Execute(
            connection,
            transaction,
            """
            INSERT INTO skill_projection_trace_heads(
                trace_id,desired_generation_id,current_generation_id,updated_at)
            VALUES($trace_id,$desired_generation_id,NULL,$updated_at)
            ON CONFLICT(trace_id) DO UPDATE SET
                desired_generation_id=excluded.desired_generation_id,
                current_generation_id=NULL,
                updated_at=excluded.updated_at;
            """,
            ("$trace_id", traceId),
            ("$desired_generation_id", resolved || inputUnavailable ? generationId : DBNull.Value),
            ("$updated_at", now));
        (workspaceParticipant ?? UnconfiguredLocalWorkspaceProjectionTransactionParticipant.Instance).RefreshSessions(
            connection, transaction, invalidatedSessions, changedAt);
        return new(compatibilityRevision, generationId, frontierDigest);
    }

    internal static IReadOnlyList<SkillProjectionFrontierInput> ReadFrontier(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DISTINCT
                source.id,
                source.raw_record_id,
                source.input_evidence_kind,
                source.raw_payload_sha256
            FROM source_trace_version_observations AS observation
            JOIN source_schema_observations AS source
              ON source.id=observation.source_observation_id
            WHERE observation.trace_id=$trace_id
            ORDER BY source.raw_record_id,source.id;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        using var reader = command.ExecuteReader();
        var inputs = new List<SkillProjectionFrontierInput>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                throw new InvalidOperationException("skill_projection_input_unavailable");
            var input = new SkillProjectionFrontierInput(
                reader.GetInt64(0),
                reader.GetInt64(1),
                SkillProjectionHashing.ParseEvidenceKind(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3));
            SkillProjectionHashing.ValidateInput(input);
            inputs.Add(input);
        }
        return inputs;
    }

    private static long EnsureAndReadCompatibilityRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        TraceSourceVersionResolutionState state,
        string? exactVersion,
        string now,
        bool bump)
    {
        var existing = ScalarLong(
            connection,
            transaction,
            "SELECT current_revision FROM source_trace_compatibility_revisions WHERE trace_id=$trace_id;",
            ("$trace_id", traceId));
        var revision = existing is null ? 0 : checked(existing.Value + (bump ? 1 : 0));
        Execute(
            connection,
            transaction,
            """
            INSERT INTO source_trace_compatibility_revisions(
                trace_id,current_revision,current_effective_state,current_exact_version,updated_at)
            VALUES($trace_id,$revision,$state,$version,$updated_at)
            ON CONFLICT(trace_id) DO UPDATE SET
                current_revision=excluded.current_revision,
                current_effective_state=excluded.current_effective_state,
                current_exact_version=excluded.current_exact_version,
                updated_at=excluded.updated_at;
            """,
            ("$trace_id", traceId),
            ("$revision", revision),
            ("$state", Wire(state)),
            ("$version", exactVersion is null ? DBNull.Value : exactVersion),
            ("$updated_at", now));
        return revision;
    }

    private static void EnsureCompatibilityRevisionExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        TraceSourceVersionResolutionState state,
        string? exactVersion,
        string now) =>
        Execute(
            connection,
            transaction,
            """
            INSERT INTO source_trace_compatibility_revisions(
                trace_id,current_revision,current_effective_state,current_exact_version,updated_at)
            VALUES($trace_id,0,$state,$version,$updated_at)
            ON CONFLICT(trace_id) DO NOTHING;
            """,
            ("$trace_id", traceId),
            ("$state", Wire(state)),
            ("$version", exactVersion is null ? DBNull.Value : exactVersion),
            ("$updated_at", now));

    private static void SupersedeGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        string now)
    {
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_generations
            SET lifecycle='superseded',updated_at=$updated_at
            WHERE generation_id=$generation_id
              AND lifecycle IN ('pending','retry_pending','current','input_unavailable','failed_terminal');
            """,
            ("$updated_at", now),
            ("$generation_id", generationId));
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_queue
            SET state='superseded',lease_owner=NULL,lease_expires_at=NULL,
                next_attempt_at=NULL,error_code=NULL
            WHERE generation_id=$generation_id
              AND state IN ('pending','leased','input_unavailable','failed_terminal');
            """,
            ("$generation_id", generationId));
    }

    private static long InsertOrReadGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        long compatibilityRevision,
        string frontierDigest,
        string projectorVersion,
        string lifecycle,
        string now)
    {
        Execute(
            connection,
            transaction,
            """
            INSERT INTO skill_projection_generations(
                trace_id,compatibility_revision,input_frontier_sha256,projector_version,
                lifecycle,created_at,updated_at)
            VALUES($trace_id,$revision,$frontier,$projector,$lifecycle,$created_at,$updated_at)
            ON CONFLICT(trace_id,compatibility_revision,input_frontier_sha256,projector_version)
            DO NOTHING;
            """,
            ("$trace_id", traceId),
            ("$revision", compatibilityRevision),
            ("$frontier", frontierDigest),
            ("$projector", projectorVersion),
            ("$lifecycle", lifecycle),
            ("$created_at", now),
            ("$updated_at", now));
        return ScalarLong(
            connection,
            transaction,
            """
            SELECT generation_id
            FROM skill_projection_generations
            WHERE trace_id=$trace_id AND compatibility_revision=$revision
              AND input_frontier_sha256=$frontier AND projector_version=$projector;
            """,
            ("$trace_id", traceId),
            ("$revision", compatibilityRevision),
            ("$frontier", frontierDigest),
            ("$projector", projectorVersion))!.Value;
    }

    private static void InsertInputs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        IReadOnlyList<SkillProjectionFrontierInput> inputs)
    {
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            Execute(
                connection,
                transaction,
                """
                INSERT INTO skill_projection_generation_inputs(
                    generation_id,input_ordinal,source_observation_id,raw_record_id,
                    input_evidence_kind,raw_payload_sha256)
                VALUES($generation_id,$ordinal,$source_observation_id,$raw_record_id,
                    $input_evidence_kind,$raw_payload_sha256)
                ON CONFLICT(generation_id,input_ordinal) DO NOTHING;
                """,
                ("$generation_id", generationId),
                ("$ordinal", index),
                ("$source_observation_id", input.SourceObservationId),
                ("$raw_record_id", input.RawRecordId),
                ("$input_evidence_kind", SkillProjectionHashing.Wire(input.EvidenceKind)),
                ("$raw_payload_sha256", input.RawPayloadSha256 is null
                    ? DBNull.Value
                    : input.RawPayloadSha256));
        }
    }

    private static void InsertQueue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        string traceId,
        long compatibilityRevision,
        string frontierDigest,
        string projectorVersion,
        string state,
        string? errorCode) =>
        Execute(
            connection,
            transaction,
            """
            INSERT INTO skill_projection_queue(
                generation_id,trace_id,compatibility_revision,input_frontier_sha256,
                projector_version,state,error_code)
            VALUES($generation_id,$trace_id,$revision,$frontier,$projector,$state,$error_code)
            ON CONFLICT(generation_id) DO NOTHING;
            """,
            ("$generation_id", generationId),
            ("$trace_id", traceId),
            ("$revision", compatibilityRevision),
            ("$frontier", frontierDigest),
            ("$projector", projectorVersion),
            ("$state", state),
            ("$error_code", errorCode is null ? DBNull.Value : errorCode));

    private static long? ScalarLong(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    internal static string Wire(TraceSourceVersionResolutionState state) =>
        state switch
        {
            TraceSourceVersionResolutionState.Resolved => "resolved",
            TraceSourceVersionResolutionState.Missing => "missing",
            TraceSourceVersionResolutionState.Conflicting => "conflicting",
            TraceSourceVersionResolutionState.Unrecognised => "unrecognised",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

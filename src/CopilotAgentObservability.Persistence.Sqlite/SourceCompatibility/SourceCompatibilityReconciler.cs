using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum SourceCompatibilityReconciliationCheckpoint
{
    AfterRetentionAdmission,
    BeforeCommit,
}

internal sealed class SourceCompatibilityReconciler
{
    private readonly string databasePath;
    private readonly SourceCompatibilityReconciliationAuthority authority;
    private readonly TimeProvider timeProvider;
    private readonly Action<SourceCompatibilityReconciliationCheckpoint>? checkpoint;
    private readonly Action<Func<RawTelemetryRecord>>? lastRawAccessObserverForTesting;

    internal SourceCompatibilityReconciler(
        string databasePath,
        Action<SourceCompatibilityReconciliationCheckpoint>? checkpoint = null)
        : this(
            databasePath,
            SourceCompatibilityReconciliationAuthority.Empty,
            TimeProvider.System,
            checkpoint)
    {
    }

    internal SourceCompatibilityReconciler(
        string databasePath,
        SourceCompatibilityReconciliationAuthority authority,
        TimeProvider timeProvider,
        Action<SourceCompatibilityReconciliationCheckpoint>? checkpoint = null,
        Action<Func<RawTelemetryRecord>>? lastRawAccessObserverForTesting = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.databasePath = databasePath;
        this.authority = authority;
        this.timeProvider = timeProvider;
        this.checkpoint = checkpoint;
        this.lastRawAccessObserverForTesting = lastRawAccessObserverForTesting;
    }

    internal SourceCompatibilityReconciliationResult Reconcile(
        SourceCompatibilityReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        CurrentInterpretation initial;
        string fingerprint;
        using (var connection = Open())
        {
            initial = ReadCurrentInterpretation(
                connection,
                transaction: null,
                request.SourceObservationId,
                request.TraceId)
                ?? throw new InvalidOperationException("source_compatibility_observation_not_found");
            fingerprint = SkillProjectionHashing.ReconciliationFingerprint(
                request,
                initial.Input);
            if (ReadReceipt(connection, transaction: null, request.OperationKey) is { } existing)
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("source_compatibility_operation_conflict");
                return existing.Result;
            }
        }
        if (!string.Equals(
                request.ProjectorVersion,
                SkillProjectionGenerationParticipant.CurrentProjectorVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("source_compatibility_projector_revision_unaccepted");
        }
        if (!authority.TryGetRegistry(
                request.ResolverRevision,
                request.RegistryRevision,
                out var registry))
        {
            throw new InvalidOperationException("source_compatibility_revision_unaccepted");
        }
        if (initial.Revision != request.ExpectedInterpretationRevision)
            throw new InvalidOperationException("source_compatibility_revision_conflict");
        ValidateTrigger(request, initial);

        if (request.Trigger == SourceCompatibilityReconciliationTrigger.DecoderRevision
            && initial.Input.EvidenceKind
               == SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10)
        {
            return CommitInputUnavailable(request, fingerprint);
        }

        if (request.Trigger == SourceCompatibilityReconciliationTrigger.RegistryRevision)
        {
            return Commit(
                request,
                fingerprint,
                registry,
                retainedRecord: null,
                retentionGrant: null);
        }

        var context = RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
        var rawStore = new RawTelemetryStore(databasePath, context, timeProvider);
        var read = rawStore
            .GetRawRecordByIdAsync(
                initial.RawRecordId,
                RetentionReadKind.Operation,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (read.Lease is not { Grant: { } grant } lease)
        {
            throw new InvalidOperationException("source_compatibility_retained_input_unavailable");
        }
        try
        {
            if (read.Disposition is not null)
            {
                _ = read.CompletePostGrantFailure();
                throw new InvalidOperationException("source_compatibility_retained_input_unavailable");
            }
            checkpoint?.Invoke(SourceCompatibilityReconciliationCheckpoint.AfterRetentionAdmission);
            using var retainedRecordReference = lease.AcquireValueReference();
            lastRawAccessObserverForTesting?.Invoke(() => retainedRecordReference.Value);
            return Commit(request, fingerprint, registry, retainedRecordReference.Value, grant);
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private SourceCompatibilityReconciliationResult CommitInputUnavailable(
        SourceCompatibilityReconciliationRequest request,
        string fingerprint)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (ReadReceipt(connection, transaction, request.OperationKey) is { } existing)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("source_compatibility_operation_conflict");
            transaction.Commit();
            return existing.Result;
        }
        var current = ReadCurrentInterpretation(
            connection,
            transaction,
            request.SourceObservationId,
            request.TraceId)
            ?? throw new InvalidOperationException("source_compatibility_observation_not_found");
        if (current.Revision != request.ExpectedInterpretationRevision)
            throw new InvalidOperationException("source_compatibility_revision_conflict");
        if (current.Input.EvidenceKind
                != SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10
            || !string.Equals(
                fingerprint,
                SkillProjectionHashing.ReconciliationFingerprint(request, current.Input),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("source_compatibility_retained_input_mismatch");
        }
        ValidateTrigger(request, current);
        var result = new SourceCompatibilityReconciliationResult(
            SourceCompatibilityReconciliationOutcome.InputUnavailable,
            SupersessionId: null,
            current.Revision,
            CompatibilityRevision: null,
            GenerationId: null);
        var committedAt = timeProvider.GetUtcNow();
        InsertReceipts(
            connection,
            transaction,
            request,
            current.Input,
            fingerprint,
            result,
            committedAt);
        checkpoint?.Invoke(SourceCompatibilityReconciliationCheckpoint.BeforeCommit);
        transaction.Commit();
        return result;
    }

    private SourceCompatibilityReconciliationResult Commit(
        SourceCompatibilityReconciliationRequest request,
        string fingerprint,
        VerifiedSourceFingerprintRegistry registry,
        RawTelemetryRecord? retainedRecord,
        RetentionReadGrant? retentionGrant)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var publications = RetentionGrantPublicationSet.EnterInOrder(
            retentionGrant is null
                ? []
                : [new RetentionGrantPublicationMember(retentionGrant, 0)]);
        var committedAt = timeProvider.GetUtcNow();
        if (ReadReceipt(connection, transaction, request.OperationKey) is { } existing)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("source_compatibility_operation_conflict");
            if (!publications.TryClaimCommittedHandles(out var existingPublicationClaim))
                throw new InvalidOperationException("source_compatibility_retained_input_unavailable");
            using (existingPublicationClaim)
                transaction.Commit();
            return existing.Result;
        }

        var current = ReadCurrentInterpretation(
            connection,
            transaction,
            request.SourceObservationId,
            request.TraceId)
            ?? throw new InvalidOperationException("source_compatibility_observation_not_found");
        if (current.Revision != request.ExpectedInterpretationRevision)
            throw new InvalidOperationException("source_compatibility_revision_conflict");
        if (retainedRecord is not null
            && (current.Input.EvidenceKind != SkillProjectionInputEvidenceKind.PayloadSha256
                || !string.Equals(
                    current.Input.RawPayloadSha256,
                    SkillProjectionHashing.InputDigest(retainedRecord.PayloadJson),
                    StringComparison.Ordinal))
            || !string.Equals(
                fingerprint,
                SkillProjectionHashing.ReconciliationFingerprint(
                    request,
                    current.Input),
                StringComparison.Ordinal))
            throw new InvalidOperationException("source_compatibility_retained_input_mismatch");
        ValidateTrigger(request, current);
        SourceCompatibilityInterpretation derived;
        if (request.Trigger == SourceCompatibilityReconciliationTrigger.DecoderRevision)
        {
            if (retainedRecord?.Id != current.RawRecordId
                || retentionGrant is null
                || !RetentionCatalogStore.ValidateSourceCompatibilityOperationLease(
                    connection,
                    transaction,
                    retentionGrant,
                    current.RawRecordId,
                    publications.ScopeFor(0, retentionGrant),
                    committedAt))
            {
                throw new InvalidOperationException("source_compatibility_retained_input_unavailable");
            }
            derived = Decode(
                retainedRecord.PayloadJson,
                current.SourceSurface,
                request.TraceId,
                registry);
        }
        else
        {
            derived = registry.RecognisesSourceVersion(
                    current.SourceSurface,
                    current.ExactVersion!)
                ? new(TraceSourceVersionResolutionState.Resolved, current.ExactVersion)
                : new(TraceSourceVersionResolutionState.Unrecognised, current.ExactVersion);
        }
        SourceCompatibilityReconciliationRequest.ValidateInterpretation(
            derived.State,
            derived.ExactVersion);

        var before = ReadEffectiveTrace(connection, transaction, request.TraceId)
            ?? throw new InvalidOperationException("source_compatibility_trace_not_found");
        EnsureTraceCompatibilityRevision(connection, transaction, request.TraceId, before, committedAt);
        if (current.State == derived.State
            && string.Equals(current.ExactVersion, derived.ExactVersion, StringComparison.Ordinal))
        {
            var compatibilityRevision = ReadCompatibilityRevision(
                connection,
                transaction,
                request.TraceId);
            var noChange = new SourceCompatibilityReconciliationResult(
                SourceCompatibilityReconciliationOutcome.NoChange,
                SupersessionId: null,
                current.Revision,
                compatibilityRevision,
                GenerationId: null);
            InsertReceipts(
                connection,
                transaction,
                request,
                current.Input,
                fingerprint,
                noChange,
                committedAt);
            checkpoint?.Invoke(SourceCompatibilityReconciliationCheckpoint.BeforeCommit);
            if (!publications.TryClaimCommittedHandles(out var noChangePublicationClaim))
                throw new InvalidOperationException("source_compatibility_retained_input_unavailable");
            using (noChangePublicationClaim)
                transaction.Commit();
            return noChange;
        }

        var newInterpretationRevision = checked(current.Revision + 1);
        var supersessionId = InsertSupersession(
            connection,
            transaction,
            request,
            fingerprint,
            derived,
            current.Input,
            current.Revision,
            newInterpretationRevision,
            committedAt);
        UpsertHead(
            connection,
            transaction,
            request.SourceObservationId,
            request.TraceId,
            current.Revision,
            newInterpretationRevision,
            supersessionId);

        var after = ReadEffectiveTrace(connection, transaction, request.TraceId)
            ?? throw new InvalidOperationException("source_compatibility_trace_not_found");
        var generation = SkillProjectionGenerationParticipant.Advance(
            connection,
            transaction,
            request.TraceId,
            after.State,
            after.SourceApplicationVersion,
            request.ProjectorVersion,
            committedAt,
            bumpCompatibilityRevision: true);
        var compatibilityRevisionAfter = ReadCompatibilityRevision(
            connection,
            transaction,
            request.TraceId);
        var changed = new SourceCompatibilityReconciliationResult(
            SourceCompatibilityReconciliationOutcome.Changed,
            supersessionId,
            newInterpretationRevision,
            compatibilityRevisionAfter,
            generation.GenerationId);
        InsertReceipts(
            connection,
            transaction,
            request,
            current.Input,
            fingerprint,
            changed,
            committedAt);
        checkpoint?.Invoke(SourceCompatibilityReconciliationCheckpoint.BeforeCommit);
        if (!publications.TryClaimCommittedHandles(out var publicationClaim))
            throw new InvalidOperationException("source_compatibility_retained_input_unavailable");
        using (publicationClaim)
            transaction.Commit();
        return changed;
    }

    private static void ValidateTrigger(
        SourceCompatibilityReconciliationRequest request,
        CurrentInterpretation current)
    {
        if (request.Trigger == SourceCompatibilityReconciliationTrigger.RegistryRevision)
        {
            if (current.State != TraceSourceVersionResolutionState.Unrecognised
                || current.ExactVersion is null)
            {
                throw new InvalidOperationException("registry_revision_exact_token_required");
            }
        }
    }

    private static SourceCompatibilityInterpretation Decode(
        string retainedPayloadJson,
        string sourceSurface,
        string traceId,
        VerifiedSourceFingerprintRegistry registry)
    {
        var resolution = OtlpTraceSourceVersionResolver
            .Resolve(retainedPayloadJson, sourceSurface, registry)
            .SingleOrDefault(candidate =>
                string.Equals(candidate.TraceId, traceId, StringComparison.Ordinal));
        return resolution is null
            ? new(TraceSourceVersionResolutionState.Missing, null)
            : new(resolution.State, resolution.SourceApplicationVersion);
    }

    private static CurrentInterpretation? ReadCurrentInterpretation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long sourceObservationId,
        string traceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                COALESCE(supersession.new_interpretation_revision,0),
                COALESCE(supersession.derived_state,base.resolution_state),
                CASE
                    WHEN supersession.supersession_id IS NULL THEN base.source_application_version
                    ELSE supersession.exact_version
                END,
                source.raw_record_id,
                source.source_surface,
                source.input_evidence_kind,
                source.raw_payload_sha256
            FROM source_trace_version_observations AS base
            JOIN source_schema_observations AS source
              ON source.id=base.source_observation_id
            LEFT JOIN source_trace_version_interpretation_heads AS head
              ON head.source_observation_id=base.source_observation_id
             AND head.trace_id=base.trace_id
            LEFT JOIN source_trace_version_interpretation_supersessions AS supersession
              ON supersession.supersession_id=head.current_supersession_id
            WHERE base.source_observation_id=$source_observation_id
              AND base.trace_id=$trace_id;
            """;
        command.Parameters.AddWithValue("$source_observation_id", sourceObservationId);
        command.Parameters.AddWithValue("$trace_id", traceId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(
                reader.GetInt64(0),
                ParseState(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3)
                    ? throw new InvalidOperationException("source_compatibility_retained_input_unavailable")
                    : reader.GetInt64(3),
                reader.IsDBNull(4)
                    ? throw new InvalidOperationException("source_compatibility_source_surface_invalid")
                    : reader.GetString(4),
                new SkillProjectionFrontierInput(
                    sourceObservationId,
                    reader.GetInt64(3),
                    reader.IsDBNull(5)
                        ? throw new InvalidOperationException("source_compatibility_retained_input_unavailable")
                        : SkillProjectionHashing.ParseEvidenceKind(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6)))
            : null;
    }

    internal static TraceSourceVersionResolutionRow? ReadEffectiveTrace(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string traceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                COALESCE(supersession.derived_state,base.resolution_state),
                CASE
                    WHEN supersession.supersession_id IS NULL THEN base.source_application_version
                    ELSE supersession.exact_version
                END
            FROM source_trace_version_observations AS base
            LEFT JOIN source_trace_version_interpretation_heads AS head
              ON head.source_observation_id=base.source_observation_id
             AND head.trace_id=base.trace_id
            LEFT JOIN source_trace_version_interpretation_supersessions AS supersession
              ON supersession.supersession_id=head.current_supersession_id
            WHERE base.trace_id=$trace_id
            ORDER BY base.source_observation_id;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        using var reader = command.ExecuteReader();
        var observations = new List<(TraceSourceVersionResolutionState State, string? Version)>();
        while (reader.Read())
            observations.Add((ParseState(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1)));
        if (observations.Count == 0) return null;
        if (observations.Any(static item => item.State == TraceSourceVersionResolutionState.Conflicting))
            return new(traceId, TraceSourceVersionResolutionState.Conflicting, null);
        var versions = observations
            .Where(static item => item.Version is not null)
            .Select(static item => item.Version!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (versions.Length > 1)
            return new(traceId, TraceSourceVersionResolutionState.Conflicting, null);
        if (observations.Any(static item => item.State == TraceSourceVersionResolutionState.Unrecognised))
            return new(
                traceId,
                TraceSourceVersionResolutionState.Unrecognised,
                versions.Length == 1 ? versions[0] : null);
        if (observations.All(static item => item.State == TraceSourceVersionResolutionState.Resolved)
            && versions.Length == 1)
            return new(traceId, TraceSourceVersionResolutionState.Resolved, versions[0]);
        return new(traceId, TraceSourceVersionResolutionState.Missing, null);
    }

    private static void EnsureTraceCompatibilityRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        TraceSourceVersionResolutionRow current,
        DateTimeOffset now) =>
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
            ("$state", SkillProjectionGenerationParticipant.Wire(current.State)),
            ("$version", current.SourceApplicationVersion is null ? DBNull.Value : current.SourceApplicationVersion),
            ("$updated_at", Timestamp(now)));

    private static long InsertSupersession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceCompatibilityReconciliationRequest request,
        string fingerprint,
        SourceCompatibilityInterpretation derived,
        SkillProjectionFrontierInput input,
        long previousRevision,
        long newRevision,
        DateTimeOffset createdAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO source_trace_version_interpretation_supersessions(
                source_observation_id,trace_id,previous_interpretation_revision,
                new_interpretation_revision,derived_state,exact_version,reason,
                raw_record_id,input_evidence_kind,raw_payload_sha256,resolver_revision,
                registry_revision,projector_version,created_at,operation_fingerprint)
            VALUES($source_observation_id,$trace_id,$previous_revision,$new_revision,
                $state,$version,$reason,$raw_record_id,$input_evidence_kind,
                $raw_payload_sha256,$resolver_revision,$registry_revision,
                $projector_version,$created_at,$fingerprint);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$source_observation_id", request.SourceObservationId);
        command.Parameters.AddWithValue("$trace_id", request.TraceId);
        command.Parameters.AddWithValue("$previous_revision", previousRevision);
        command.Parameters.AddWithValue("$new_revision", newRevision);
        command.Parameters.AddWithValue("$state", SkillProjectionGenerationParticipant.Wire(derived.State));
        command.Parameters.AddWithValue("$version", derived.ExactVersion is null ? DBNull.Value : derived.ExactVersion);
        command.Parameters.AddWithValue(
            "$reason",
            request.Trigger == SourceCompatibilityReconciliationTrigger.DecoderRevision
                ? "decoder_revision"
                : "registry_revision");
        command.Parameters.AddWithValue("$raw_record_id", input.RawRecordId);
        command.Parameters.AddWithValue(
            "$input_evidence_kind",
            SkillProjectionHashing.Wire(input.EvidenceKind));
        command.Parameters.AddWithValue(
            "$raw_payload_sha256",
            input.RawPayloadSha256 is null ? DBNull.Value : input.RawPayloadSha256);
        command.Parameters.AddWithValue("$resolver_revision", request.ResolverRevision);
        command.Parameters.AddWithValue("$registry_revision", request.RegistryRevision);
        command.Parameters.AddWithValue("$projector_version", request.ProjectorVersion);
        command.Parameters.AddWithValue("$created_at", Timestamp(createdAt));
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpsertHead(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceObservationId,
        string traceId,
        long previousRevision,
        long newRevision,
        long supersessionId)
    {
        if (previousRevision == 0)
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO source_trace_version_interpretation_heads(
                    source_observation_id,trace_id,current_interpretation_revision,current_supersession_id)
                VALUES($source_observation_id,$trace_id,$revision,$supersession_id);
                """,
                ("$source_observation_id", sourceObservationId),
                ("$trace_id", traceId),
                ("$revision", newRevision),
                ("$supersession_id", supersessionId));
            return;
        }
        Execute(
            connection,
            transaction,
            """
            UPDATE source_trace_version_interpretation_heads
            SET current_interpretation_revision=$new_revision,
                current_supersession_id=$supersession_id
            WHERE source_observation_id=$source_observation_id
              AND trace_id=$trace_id
              AND current_interpretation_revision=$previous_revision;
            """,
            ("$new_revision", newRevision),
            ("$supersession_id", supersessionId),
            ("$source_observation_id", sourceObservationId),
            ("$trace_id", traceId),
            ("$previous_revision", previousRevision));
        if (Changes(connection, transaction) != 1)
            throw new InvalidOperationException("source_compatibility_revision_conflict");
    }

    private static void InsertReceipts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceCompatibilityReconciliationRequest request,
        SkillProjectionFrontierInput input,
        string fingerprint,
        SourceCompatibilityReconciliationResult result,
        DateTimeOffset createdAt)
    {
        var outcome = result.Outcome switch
        {
            SourceCompatibilityReconciliationOutcome.Changed => "changed",
            SourceCompatibilityReconciliationOutcome.NoChange => "no_change",
            SourceCompatibilityReconciliationOutcome.InputUnavailable => "input_unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        Execute(
            connection,
            transaction,
            """
            INSERT INTO source_compatibility_reconciliation_receipts(
                operation_key,request_fingerprint,source_observation_id,trace_id,
                expected_interpretation_revision,raw_record_id,input_evidence_kind,
                raw_payload_sha256,resolver_revision,registry_revision,projector_version,
                outcome,resulting_supersession_id,
                resulting_interpretation_revision,resulting_compatibility_revision,
                resulting_generation_id,created_at)
            VALUES($operation_key,$fingerprint,$source_observation_id,$trace_id,
                $expected_revision,$raw_record_id,$input_evidence_kind,$raw_payload_sha256,
                $resolver_revision,$registry_revision,$projector_version,$outcome,
                $supersession_id,$interpretation_revision,
                $compatibility_revision,$generation_id,$created_at);
            """,
            ("$operation_key", request.OperationKey),
            ("$fingerprint", fingerprint),
            ("$source_observation_id", request.SourceObservationId),
            ("$trace_id", request.TraceId),
            ("$expected_revision", request.ExpectedInterpretationRevision),
            ("$raw_record_id", input.RawRecordId),
            ("$input_evidence_kind", SkillProjectionHashing.Wire(input.EvidenceKind)),
            ("$raw_payload_sha256", input.RawPayloadSha256 is null
                ? DBNull.Value
                : input.RawPayloadSha256),
            ("$resolver_revision", request.ResolverRevision),
            ("$registry_revision", request.RegistryRevision),
            ("$projector_version", request.ProjectorVersion),
            ("$outcome", outcome),
            ("$supersession_id", result.SupersessionId is null ? DBNull.Value : result.SupersessionId.Value),
            ("$interpretation_revision", result.InterpretationRevision),
            ("$compatibility_revision", result.CompatibilityRevision is null ? DBNull.Value : result.CompatibilityRevision.Value),
            ("$generation_id", result.GenerationId is null ? DBNull.Value : result.GenerationId.Value),
            ("$created_at", Timestamp(createdAt)));
        Execute(
            connection,
            transaction,
            """
            INSERT INTO skill_projection_operation_receipts(
                operation_key,semantic_fingerprint,outcome,generation_id,created_at)
            VALUES($operation_key,$fingerprint,$outcome,$generation_id,$created_at);
            """,
            ("$operation_key", request.OperationKey),
            ("$fingerprint", fingerprint),
            ("$outcome", outcome),
            ("$generation_id", result.GenerationId is null ? DBNull.Value : result.GenerationId.Value),
            ("$created_at", Timestamp(createdAt)));
    }

    private static StoredReceipt? ReadReceipt(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT request_fingerprint,outcome,resulting_supersession_id,
                   resulting_interpretation_revision,resulting_compatibility_revision,
                   resulting_generation_id
            FROM source_compatibility_reconciliation_receipts
            WHERE operation_key=$operation_key;
            """;
        command.Parameters.AddWithValue("$operation_key", operationKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(
            reader.GetString(0),
            new(
                reader.GetString(1) switch
                {
                    "changed" => SourceCompatibilityReconciliationOutcome.Changed,
                    "no_change" => SourceCompatibilityReconciliationOutcome.NoChange,
                    "input_unavailable" => SourceCompatibilityReconciliationOutcome.InputUnavailable,
                    _ => throw new InvalidOperationException("source_compatibility_receipt_outcome_invalid"),
                },
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
    }

    private static long ReadCompatibilityRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT current_revision FROM source_trace_compatibility_revisions WHERE trace_id=$trace_id;";
        command.Parameters.AddWithValue("$trace_id", traceId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long Changes(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT changes();";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static TraceSourceVersionResolutionState ParseState(string value) =>
        value switch
        {
            "resolved" => TraceSourceVersionResolutionState.Resolved,
            "missing" => TraceSourceVersionResolutionState.Missing,
            "conflicting" => TraceSourceVersionResolutionState.Conflicting,
            "unrecognised" => TraceSourceVersionResolutionState.Unrecognised,
            _ => throw new InvalidOperationException("source_compatibility_state_invalid"),
        };

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record CurrentInterpretation(
        long Revision,
        TraceSourceVersionResolutionState State,
        string? ExactVersion,
        long RawRecordId,
        string SourceSurface,
        SkillProjectionFrontierInput Input);

    private sealed record SourceCompatibilityInterpretation(
        TraceSourceVersionResolutionState State,
        string? ExactVersion);

    private sealed record StoredReceipt(
        string Fingerprint,
        SourceCompatibilityReconciliationResult Result);
}

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class SourceCompatibilitySchemaV11
{
    internal static IReadOnlyList<(string Name, string Table, string Sql)> TriggerDefinitions { get; } =
    [
        (
            "source_schema_observations_insert_no_replace",
            "source_schema_observations",
            """
            CREATE TRIGGER source_schema_observations_insert_no_replace
            BEFORE INSERT ON source_schema_observations
            WHEN EXISTS(
                SELECT 1 FROM source_schema_observations
                WHERE id=NEW.id
                   OR observation_id=NEW.observation_id COLLATE BINARY
                   OR (NEW.raw_record_id IS NOT NULL AND raw_record_id=NEW.raw_record_id)
                   OR (NEW.ingest_batch_id IS NOT NULL AND ingest_batch_id=NEW.ingest_batch_id COLLATE BINARY)
            )
            BEGIN SELECT RAISE(ABORT,'source_schema_observation_no_replace'); END;
            """),
        (
            "source_trace_version_observations_update_rejected",
            "source_trace_version_observations",
            "CREATE TRIGGER source_trace_version_observations_update_rejected BEFORE UPDATE ON source_trace_version_observations BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_immutable'); END;"),
        (
            "source_trace_version_observations_insert_no_replace",
            "source_trace_version_observations",
            """
            CREATE TRIGGER source_trace_version_observations_insert_no_replace
            BEFORE INSERT ON source_trace_version_observations
            WHEN EXISTS(
                SELECT 1 FROM source_trace_version_observations
                WHERE source_observation_id=NEW.source_observation_id
                  AND trace_id=NEW.trace_id COLLATE BINARY
            )
            BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_no_replace'); END;
            """),
        (
            "source_trace_version_observations_delete_rejected",
            "source_trace_version_observations",
            "CREATE TRIGGER source_trace_version_observations_delete_rejected BEFORE DELETE ON source_trace_version_observations BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_immutable'); END;"),
        (
            "source_schema_observations_trace_version_child_delete_rejected",
            "source_schema_observations",
            """
            CREATE TRIGGER source_schema_observations_trace_version_child_delete_rejected
            BEFORE DELETE ON source_schema_observations
            WHEN EXISTS(
                SELECT 1 FROM source_trace_version_observations
                WHERE source_observation_id=OLD.id
            )
            BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_parent_restricted'); END;
            """),
        (
            "source_schema_observations_projection_input_update_rejected",
            "source_schema_observations",
            """
            CREATE TRIGGER source_schema_observations_projection_input_update_rejected
            BEFORE UPDATE OF input_evidence_kind,raw_payload_sha256 ON source_schema_observations
            WHEN OLD.input_evidence_kind IS NOT NEW.input_evidence_kind
              OR OLD.raw_payload_sha256 IS NOT NEW.raw_payload_sha256
            BEGIN SELECT RAISE(ABORT,'source_projection_input_immutable'); END;
            """),
        (
            "source_trace_version_interpretation_supersessions_update_rejected",
            "source_trace_version_interpretation_supersessions",
            "CREATE TRIGGER source_trace_version_interpretation_supersessions_update_rejected BEFORE UPDATE ON source_trace_version_interpretation_supersessions BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;"),
        (
            "source_trace_version_interpretation_supersessions_insert_no_replace",
            "source_trace_version_interpretation_supersessions",
            """
            CREATE TRIGGER source_trace_version_interpretation_supersessions_insert_no_replace
            BEFORE INSERT ON source_trace_version_interpretation_supersessions
            WHEN EXISTS(
                SELECT 1 FROM source_trace_version_interpretation_supersessions
                WHERE supersession_id=NEW.supersession_id
                   OR (source_observation_id=NEW.source_observation_id
                       AND trace_id=NEW.trace_id COLLATE BINARY
                       AND new_interpretation_revision=NEW.new_interpretation_revision)
            )
            BEGIN SELECT RAISE(ABORT,'source_compatibility_supersession_no_replace'); END;
            """),
        (
            "source_trace_version_interpretation_supersessions_delete_rejected",
            "source_trace_version_interpretation_supersessions",
            "CREATE TRIGGER source_trace_version_interpretation_supersessions_delete_rejected BEFORE DELETE ON source_trace_version_interpretation_supersessions BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;"),
        (
            "source_compatibility_reconciliation_receipts_update_rejected",
            "source_compatibility_reconciliation_receipts",
            "CREATE TRIGGER source_compatibility_reconciliation_receipts_update_rejected BEFORE UPDATE ON source_compatibility_reconciliation_receipts BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;"),
        (
            "source_compatibility_reconciliation_receipts_insert_no_replace",
            "source_compatibility_reconciliation_receipts",
            """
            CREATE TRIGGER source_compatibility_reconciliation_receipts_insert_no_replace
            BEFORE INSERT ON source_compatibility_reconciliation_receipts
            WHEN EXISTS(
                SELECT 1 FROM source_compatibility_reconciliation_receipts
                WHERE operation_key=NEW.operation_key COLLATE BINARY
            )
            BEGIN SELECT RAISE(ABORT,'source_compatibility_receipt_no_replace'); END;
            """),
        (
            "source_compatibility_reconciliation_receipts_delete_rejected",
            "source_compatibility_reconciliation_receipts",
            "CREATE TRIGGER source_compatibility_reconciliation_receipts_delete_rejected BEFORE DELETE ON source_compatibility_reconciliation_receipts BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;"),
        (
            "source_trace_version_interpretation_heads_delete_rejected",
            "source_trace_version_interpretation_heads",
            "CREATE TRIGGER source_trace_version_interpretation_heads_delete_rejected BEFORE DELETE ON source_trace_version_interpretation_heads BEGIN SELECT RAISE(ABORT,'source_compatibility_head_delete_rejected'); END;"),
        (
            "source_trace_version_interpretation_heads_insert_no_replace",
            "source_trace_version_interpretation_heads",
            """
            CREATE TRIGGER source_trace_version_interpretation_heads_insert_no_replace
            BEFORE INSERT ON source_trace_version_interpretation_heads
            WHEN EXISTS(
                SELECT 1 FROM source_trace_version_interpretation_heads
                WHERE (source_observation_id=NEW.source_observation_id
                       AND trace_id=NEW.trace_id COLLATE BINARY)
                   OR current_supersession_id=NEW.current_supersession_id
            )
            BEGIN SELECT RAISE(ABORT,'source_compatibility_head_no_replace'); END;
            """),
        (
            "source_trace_version_interpretation_heads_update_guard",
            "source_trace_version_interpretation_heads",
            """
            CREATE TRIGGER source_trace_version_interpretation_heads_update_guard
            BEFORE UPDATE ON source_trace_version_interpretation_heads
            WHEN NOT EXISTS(
                SELECT 1
                FROM source_trace_version_interpretation_supersessions AS supersession
                WHERE supersession.supersession_id=NEW.current_supersession_id
                  AND supersession.source_observation_id=OLD.source_observation_id
                  AND supersession.trace_id=OLD.trace_id
                  AND supersession.previous_interpretation_revision=OLD.current_interpretation_revision
                  AND supersession.new_interpretation_revision=NEW.current_interpretation_revision
            )
            BEGIN
                SELECT RAISE(ABORT,'source_compatibility_head_transition_invalid');
            END;
            """),
    ];

    internal static readonly string[] OwnedObjectNames =
    [
        "source_trace_version_interpretation_supersessions",
        "source_trace_version_interpretation_heads",
        "source_trace_compatibility_revisions",
        "source_compatibility_reconciliation_receipts",
        "source_schema_observations_insert_no_replace",
        "source_trace_version_observations_update_rejected",
        "source_trace_version_observations_insert_no_replace",
        "source_trace_version_observations_delete_rejected",
        "source_schema_observations_trace_version_child_delete_rejected",
        "source_schema_observations_projection_input_update_rejected",
        "source_trace_version_interpretation_supersessions_update_rejected",
        "source_trace_version_interpretation_supersessions_insert_no_replace",
        "source_trace_version_interpretation_supersessions_delete_rejected",
        "source_compatibility_reconciliation_receipts_update_rejected",
        "source_compatibility_reconciliation_receipts_insert_no_replace",
        "source_compatibility_reconciliation_receipts_delete_rejected",
        "source_trace_version_interpretation_heads_delete_rejected",
        "source_trace_version_interpretation_heads_insert_no_replace",
        "source_trace_version_interpretation_heads_update_guard",
    ];
    private static readonly string[] AuthorityTableNames =
    [
        "source_trace_version_interpretation_supersessions",
        "source_trace_version_interpretation_heads",
        "source_trace_compatibility_revisions",
        "source_compatibility_reconciliation_receipts",
    ];
    private static readonly string[] AuthorityNamespacePrefixes =
    [
        "source_trace_version_interpretation_",
        "source_trace_compatibility_",
        "source_compatibility_reconciliation_",
    ];
    private static readonly Lazy<
        IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>>
        ExpectedOwnedObjects = new(BuildExpectedOwnedObjects);

    internal static void Ensure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? predecessorVersion = null)
    {
        EnsureBaseTables(connection, transaction);
        EnsureRestrictedTraceVersionTable(connection, transaction);
        EnsureDurableProjectionInputs(connection, transaction, predecessorVersion);
        EnsureBaseIndexesAndTriggers(connection, transaction);
        SourceCompatibilityReconciliationSchema.Ensure(connection, transaction);
        EnsureBaseTraceCompatibilityRevisions(connection, transaction);
    }

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!SqliteOwnedSchemaAuthority.Equal(
                ReadOwnedObjects(connection, transaction),
                ExpectedOwnedObjects.Value))
        {
            throw new InvalidOperationException(
                "Unsupported incomplete monitor schema version 11.");
        }
        foreach (var name in OwnedObjectNames)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name=$name;";
            command.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Unsupported incomplete monitor schema version 11.");
        }
        ValidateDurableProjectionColumnDefinitions(connection, transaction);
        using (var foreignKey = connection.CreateCommand())
        {
            foreignKey.Transaction = transaction;
            foreignKey.CommandText = "PRAGMA foreign_key_list(source_trace_version_observations);";
            using var reader = foreignKey.ExecuteReader();
            var restricted = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(2), "source_schema_observations", StringComparison.Ordinal)
                    && string.Equals(reader.GetString(5), "RESTRICT", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(reader.GetString(6), "RESTRICT", StringComparison.OrdinalIgnoreCase))
                {
                    restricted = true;
                }
            }
            if (!restricted)
                throw new InvalidOperationException("Unsupported incomplete monitor schema version 11.");
        }
        ValidatePositiveSourceIdentities(connection, transaction);
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM source_trace_version_interpretation_supersessions AS current
                WHERE current.previous_interpretation_revision > 0
                  AND NOT EXISTS(
                        SELECT 1
                        FROM source_trace_version_interpretation_supersessions AS previous
                        WHERE previous.source_observation_id=current.source_observation_id
                          AND previous.trace_id=current.trace_id
                          AND previous.new_interpretation_revision=current.previous_interpretation_revision
                  );
                """)
            || Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM source_trace_version_interpretation_heads AS head
                LEFT JOIN source_trace_version_interpretation_supersessions AS supersession
                  ON supersession.supersession_id=head.current_supersession_id
                 AND supersession.source_observation_id=head.source_observation_id
                 AND supersession.trace_id=head.trace_id
                 AND supersession.new_interpretation_revision=head.current_interpretation_revision
                WHERE supersession.supersession_id IS NULL;
                """)
            || Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM source_trace_version_interpretation_supersessions AS supersession
                LEFT JOIN source_trace_version_interpretation_heads AS head
                  ON head.source_observation_id=supersession.source_observation_id
                 AND head.trace_id=supersession.trace_id
                WHERE head.current_supersession_id IS NULL
                   OR head.current_interpretation_revision<>(
                        SELECT MAX(candidate.new_interpretation_revision)
                        FROM source_trace_version_interpretation_supersessions AS candidate
                        WHERE candidate.source_observation_id=supersession.source_observation_id
                          AND candidate.trace_id=supersession.trace_id
                   );
                """))
        {
            throw new InvalidOperationException("source_compatibility_revision_chain_invalid");
        }
        ValidateSupersessionTransitions(connection, transaction);
        ValidateProjectionInputs(connection, transaction);
        ValidateReconciliationFingerprints(connection, transaction);
        ValidateEffectiveTraceAuthority(connection, transaction);
        ValidateCanonicalRows(connection, transaction);
    }

    private static void ValidateSupersessionTransitions(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM source_trace_version_interpretation_supersessions AS current
                JOIN source_trace_version_observations AS base
                  ON base.source_observation_id=current.source_observation_id
                 AND base.trace_id=current.trace_id
                LEFT JOIN source_trace_version_interpretation_supersessions AS previous
                  ON previous.source_observation_id=current.source_observation_id
                 AND previous.trace_id=current.trace_id
                 AND previous.new_interpretation_revision=
                     current.previous_interpretation_revision
                WHERE (
                        current.reason='decoder_revision'
                        AND current.input_evidence_kind<>'payload_sha256'
                      )
                   OR (
                        current.reason='registry_revision'
                        AND (
                            current.derived_state<>'resolved'
                            OR current.exact_version IS NULL
                            OR NOT (
                                (
                                    current.previous_interpretation_revision=0
                                    AND base.resolution_state='unrecognised'
                                    AND base.source_application_version IS current.exact_version COLLATE BINARY
                                )
                                OR (
                                    current.previous_interpretation_revision>0
                                    AND previous.derived_state='unrecognised'
                                    AND previous.exact_version IS current.exact_version COLLATE BINARY
                                )
                            )
                        )
                      );
                """))
        {
            throw new InvalidOperationException(
                "source_compatibility_supersession_transition_invalid");
        }
    }

    private static void ValidatePositiveSourceIdentities(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1 FROM source_schema_observations
                WHERE id <= 0
                   OR raw_record_id <= 0
                UNION ALL
                SELECT 1 FROM source_unknown_observations
                WHERE id <= 0 OR source_observation_id <= 0
                UNION ALL
                SELECT 1 FROM source_trace_version_observations
                WHERE source_observation_id <= 0
                UNION ALL
                SELECT 1 FROM source_trace_version_interpretation_supersessions
                WHERE supersession_id <= 0
                   OR source_observation_id <= 0
                   OR raw_record_id <= 0
                UNION ALL
                SELECT 1 FROM source_trace_version_interpretation_heads
                WHERE source_observation_id <= 0
                   OR current_supersession_id <= 0
                UNION ALL
                SELECT 1 FROM source_compatibility_reconciliation_receipts
                WHERE source_observation_id <= 0
                   OR raw_record_id <= 0
                   OR resulting_supersession_id <= 0
                   OR resulting_generation_id <= 0
                LIMIT 1;
                """))
            throw new InvalidOperationException("source_compatibility_identity_invalid");
    }

    private static void ValidateReconciliationFingerprints(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var receipts = new List<ReconciliationReceiptValidationRow>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT receipt.operation_key,receipt.request_fingerprint,
                       receipt.source_observation_id,receipt.trace_id,
                       receipt.expected_interpretation_revision,
                       receipt.raw_record_id,receipt.input_evidence_kind,
                       receipt.raw_payload_sha256,receipt.resolver_revision,
                       receipt.registry_revision,receipt.projector_version,
                       receipt.outcome,receipt.resulting_supersession_id,
                       receipt.resulting_interpretation_revision,
                       receipt.resulting_compatibility_revision,
                       receipt.resulting_generation_id,
                       source.raw_record_id,source.input_evidence_kind,
                       source.raw_payload_sha256
                FROM source_compatibility_reconciliation_receipts AS receipt
                JOIN source_schema_observations AS source
                  ON source.id=receipt.source_observation_id
                ORDER BY receipt.operation_key;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                receipts.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    reader.GetInt64(13),
                    reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    reader.IsDBNull(16) ? null : reader.GetInt64(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18)));
            }
        }
        foreach (var receipt in receipts)
        {
            var input = new SkillProjectionFrontierInput(
                receipt.SourceObservationId,
                receipt.RawRecordId,
                SkillProjectionHashing.ParseEvidenceKind(receipt.EvidenceKind),
                receipt.RawPayloadSha256);
            SkillProjectionHashing.ValidateInput(input);
            var request = CreateValidationRequest(
                receipt.OperationKey,
                receipt.SourceObservationId,
                receipt.TraceId,
                receipt.ExpectedInterpretationRevision,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                receipt.ResolverRevision,
                receipt.RegistryRevision,
                receipt.ProjectorVersion);
            if (receipt.SourceRawRecordId != receipt.RawRecordId
                || !string.Equals(
                    receipt.SourceEvidenceKind,
                    receipt.EvidenceKind,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.SourceRawPayloadSha256,
                    receipt.RawPayloadSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    SkillProjectionHashing.ReconciliationFingerprint(request, input),
                    receipt.Fingerprint,
                    StringComparison.Ordinal)
                || receipt.Outcome == "input_unavailable"
                   && (input.EvidenceKind
                           != SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10
                       || receipt.ResultingSupersessionId is not null
                       || receipt.ResultingInterpretationRevision
                          != receipt.ExpectedInterpretationRevision
                       || receipt.ResultingCompatibilityRevision is not null
                       || receipt.ResultingGenerationId is not null)
                || receipt.Outcome == "no_change"
                   && (receipt.ResultingSupersessionId is not null
                       || receipt.ResultingGenerationId is not null)
                || receipt.Outcome == "changed"
                   && (receipt.ResultingSupersessionId is null
                       || receipt.ResultingGenerationId is null))
            {
                throw new InvalidOperationException(
                    "source_compatibility_reconciliation_fingerprint_invalid");
            }
            if (!TableExists(
                    connection,
                    transaction,
                    "skill_projection_operation_receipts")
                || !Exists(
                    connection,
                    transaction,
                    """
                    SELECT 1
                    FROM skill_projection_operation_receipts
                    WHERE operation_key=$operation_key
                      AND semantic_fingerprint=$fingerprint COLLATE BINARY
                      AND outcome=$outcome COLLATE BINARY
                      AND generation_id IS $generation_id;
                    """,
                    ("$operation_key", receipt.OperationKey),
                    ("$fingerprint", receipt.Fingerprint),
                    ("$outcome", receipt.Outcome),
                    ("$generation_id", receipt.ResultingGenerationId is null
                        ? DBNull.Value
                        : receipt.ResultingGenerationId.Value)))
            {
                throw new InvalidOperationException(
                    "source_compatibility_reconciliation_receipt_pair_invalid");
            }
        }

        using var supersessions = connection.CreateCommand();
        supersessions.Transaction = transaction;
        supersessions.CommandText =
            """
            SELECT supersession.source_observation_id,supersession.trace_id,
                   supersession.previous_interpretation_revision,
                   supersession.raw_record_id,supersession.input_evidence_kind,
                   supersession.raw_payload_sha256,supersession.resolver_revision,
                   supersession.registry_revision,supersession.projector_version,
                   supersession.operation_fingerprint,
                   source.raw_record_id,source.input_evidence_kind,
                   source.raw_payload_sha256
            FROM source_trace_version_interpretation_supersessions AS supersession
            JOIN source_schema_observations AS source
              ON source.id=supersession.source_observation_id
            ORDER BY supersession.supersession_id;
            """;
        using var supersessionReader = supersessions.ExecuteReader();
        while (supersessionReader.Read())
        {
            var sourceObservationId = supersessionReader.GetInt64(0);
            var rawRecordId = supersessionReader.GetInt64(3);
            var evidenceKind = supersessionReader.GetString(4);
            var digest = supersessionReader.IsDBNull(5)
                ? null
                : supersessionReader.GetString(5);
            var input = new SkillProjectionFrontierInput(
                sourceObservationId,
                rawRecordId,
                SkillProjectionHashing.ParseEvidenceKind(evidenceKind),
                digest);
            SkillProjectionHashing.ValidateInput(input);
            var request = CreateValidationRequest(
                "validation",
                sourceObservationId,
                supersessionReader.GetString(1),
                supersessionReader.GetInt64(2),
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                supersessionReader.GetString(6),
                supersessionReader.GetString(7),
                supersessionReader.GetString(8));
            if (supersessionReader.GetInt64(10) != rawRecordId
                || !string.Equals(
                    supersessionReader.GetString(11),
                    evidenceKind,
                    StringComparison.Ordinal)
                || !NullableExact(supersessionReader, 12, digest)
                || !string.Equals(
                    SkillProjectionHashing.ReconciliationFingerprint(request, input),
                    supersessionReader.GetString(9),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "source_compatibility_reconciliation_fingerprint_invalid");
            }
        }
    }

    private static SourceCompatibilityReconciliationRequest CreateValidationRequest(
        string operationKey,
        long sourceObservationId,
        string traceId,
        long expectedInterpretationRevision,
        SourceCompatibilityReconciliationTrigger trigger,
        string resolverRevision,
        string registryRevision,
        string projectorVersion)
    {
        try
        {
            return SourceCompatibilityReconciliationRequest.Create(
                operationKey,
                sourceObservationId,
                traceId,
                expectedInterpretationRevision,
                trigger,
                resolverRevision,
                registryRevision,
                projectorVersion);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "source_compatibility_reconciliation_fingerprint_invalid",
                exception);
        }
    }

    private static void ValidateProjectionInputs(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                source.raw_record_id,
                source.input_evidence_kind,
                source.raw_payload_sha256,
                EXISTS(
                    SELECT 1
                    FROM source_trace_version_observations AS trace
                    WHERE trace.source_observation_id=source.id),
                raw.id IS NOT NULL,
                typeof(raw.payload_json),
                CASE WHEN typeof(raw.payload_json)='text' THEN raw.payload_json END
            FROM source_schema_observations AS source
            LEFT JOIN raw_records AS raw
              ON raw.id=source.raw_record_id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var hasRaw = !reader.IsDBNull(0);
            var evidenceKind = reader.IsDBNull(1) ? null : reader.GetString(1);
            var hasDigest = !reader.IsDBNull(2);
            var hasProjectionChild = reader.GetInt64(3) != 0;
            var rawRowExists = reader.GetInt64(4) != 0;
            var rawPayloadStorageClass = reader.GetString(5);
            var rawPayload = reader.IsDBNull(6) ? null : reader.GetString(6);
            if (!hasRaw && (evidenceKind is not null || hasDigest || hasProjectionChild)
                || hasProjectionChild && evidenceKind is null
                || rawRowExists
                   && !string.Equals(
                       rawPayloadStorageClass,
                       "text",
                       StringComparison.Ordinal))
                throw new InvalidOperationException("source_projection_input_authority_invalid");
            if (evidenceKind is null)
            {
                if (hasDigest)
                    throw new InvalidOperationException("source_projection_input_authority_invalid");
                continue;
            }
            if (string.Equals(evidenceKind, "payload_sha256", StringComparison.Ordinal))
            {
                if (!hasDigest)
                    throw new InvalidOperationException("source_projection_input_authority_invalid");
                var digest = reader.GetString(2);
                if (!IsLowercaseHash(digest)
                    || rawRowExists
                       && !string.Equals(
                        digest,
                        SkillProjectionHashing.InputDigest(rawPayload!),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("source_projection_input_authority_invalid");
            }
            else if (!string.Equals(
                         evidenceKind,
                         "deleted_before_digest_v10",
                         StringComparison.Ordinal)
                     || hasDigest
                     || rawRowExists
                     || !hasProjectionChild)
            {
                throw new InvalidOperationException("source_projection_input_authority_invalid");
            }
        }
    }

    private static void ValidateEffectiveTraceAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var traceIds = new List<string>();
        using (var traces = connection.CreateCommand())
        {
            traces.Transaction = transaction;
            traces.CommandText =
                """
                SELECT DISTINCT trace_id
                FROM source_trace_version_observations
                ORDER BY trace_id;
                """;
            using var reader = traces.ExecuteReader();
            while (reader.Read())
                traceIds.Add(reader.GetString(0));
        }
        foreach (var traceId in traceIds)
        {
            var effective = SourceCompatibilityReconciler.ReadEffectiveTrace(
                connection,
                transaction,
                traceId)
                ?? throw new InvalidOperationException(
                    "source_compatibility_effective_trace_invalid");
            using var revision = connection.CreateCommand();
            revision.Transaction = transaction;
            revision.CommandText =
                """
                SELECT current_effective_state,current_exact_version
                FROM source_trace_compatibility_revisions
                WHERE trace_id=$trace_id;
                """;
            revision.Parameters.AddWithValue("$trace_id", traceId);
            using var reader = revision.ExecuteReader();
            if (!reader.Read()
                || !string.Equals(
                    reader.GetString(0),
                    SkillProjectionGenerationParticipant.Wire(effective.State),
                    StringComparison.Ordinal)
                || !NullableExact(
                    reader,
                    1,
                    effective.SourceApplicationVersion)
                || reader.Read())
            {
                throw new InvalidOperationException(
                    "source_compatibility_effective_trace_invalid");
            }
        }
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM source_trace_compatibility_revisions AS revision
                WHERE NOT EXISTS(
                    SELECT 1
                    FROM source_trace_version_observations AS observation
                    WHERE observation.trace_id=revision.trace_id
                );
                """))
        {
            throw new InvalidOperationException(
                "source_compatibility_effective_trace_invalid");
        }
    }

    private static void ValidateCanonicalRows(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        ValidateTextColumns(
            connection,
            transaction,
            """
            SELECT source_application_version
            FROM source_trace_version_observations
            WHERE source_application_version IS NOT NULL;
            """,
            TextRule.VisibleToken);
        ValidateTextColumns(
            connection,
            transaction,
            """
            SELECT exact_version
            FROM source_trace_version_interpretation_supersessions
            WHERE exact_version IS NOT NULL
            UNION ALL
            SELECT resolver_revision
            FROM source_trace_version_interpretation_supersessions
            UNION ALL
            SELECT registry_revision
            FROM source_trace_version_interpretation_supersessions;
            """,
            TextRule.RevisionToken);
        ValidateTextColumns(
            connection,
            transaction,
            """
            SELECT observed_at FROM source_schema_observations
            UNION ALL
            SELECT created_at FROM source_trace_version_interpretation_supersessions
            UNION ALL
            SELECT updated_at FROM source_trace_compatibility_revisions
            UNION ALL
            SELECT created_at FROM source_compatibility_reconciliation_receipts;
            """,
            TextRule.CanonicalTimestamp);
        ValidateTextColumns(
            connection,
            transaction,
            """
            SELECT operation_key
            FROM source_compatibility_reconciliation_receipts;
            """,
            TextRule.RevisionToken);
    }

    internal static void RejectCollidingAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        bool allowDeletedProjectionInput = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE name COLLATE NOCASE IN ({string.Join(',', OwnedObjectNames.Select(static (_, index) => $"$name{index}"))});
            """;
        for (var index = 0; index < OwnedObjectNames.Length; index++)
            command.Parameters.AddWithValue($"$name{index}", OwnedObjectNames[index]);
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0
            || ColumnExists(
                connection,
                transaction,
                "source_schema_observations",
                "input_evidence_kind")
            || ColumnExists(
                connection,
                transaction,
                "source_schema_observations",
                "raw_payload_sha256")
            || ColumnExists(
                connection,
                transaction,
                "source_schema_observations",
                "raw_owner_identity")
            || HasUnrecoverableProjectionInput(
                connection,
                transaction,
                allowDeletedProjectionInput))
            throw new InvalidOperationException("Unsupported incomplete monitor schema version 11.");
    }

    private static bool HasUnrecoverableProjectionInput(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        bool allowDeletedProjectionInput)
    {
        var unavailablePredicate = allowDeletedProjectionInput
            ? "(source.raw_record_id IS NULL OR (raw.id IS NOT NULL AND typeof(raw.payload_json)<>'text'))"
            : "(source.raw_record_id IS NULL OR raw.id IS NULL OR typeof(raw.payload_json)<>'text')";
        return TableExists(connection, transaction, "source_schema_observations")
            && TableExists(connection, transaction, "source_trace_version_observations")
            && TableExists(connection, transaction, "raw_records")
            && Exists(
                connection,
                transaction,
                $"""
            SELECT 1
            FROM source_schema_observations AS source
            LEFT JOIN raw_records AS raw
              ON raw.id=source.raw_record_id
            WHERE EXISTS(
                    SELECT 1
                    FROM source_trace_version_observations AS trace
                    WHERE trace.source_observation_id=source.id)
              AND {unavailablePredicate};
            """);
    }

    private static void ValidateDurableProjectionColumnDefinitions(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT sql
            FROM sqlite_master
            WHERE type='table' AND name='source_schema_observations';
            """;
        var tableSql = command.ExecuteScalar() as string
            ?? throw new InvalidOperationException(
                "Unsupported incomplete monitor schema version 11.");
        var evidenceKind = ReadColumnDefinition(tableSql, "input_evidence_kind");
        var digest = ReadColumnDefinition(tableSql, "raw_payload_sha256");
        if (!string.Equals(
                NormalizeColumnDefinition(evidenceKind),
                NormalizeColumnDefinition(
                    """
                    input_evidence_kind TEXT NULL CHECK(
                        (input_evidence_kind IS NULL AND raw_payload_sha256 IS NULL)
                        OR (input_evidence_kind='payload_sha256' AND raw_payload_sha256 IS NOT NULL)
                        OR (input_evidence_kind='deleted_before_digest_v10' AND raw_payload_sha256 IS NULL)
                    )
                    """),
                StringComparison.Ordinal)
            || !string.Equals(
                NormalizeColumnDefinition(digest),
                NormalizeColumnDefinition(
                    """
                    raw_payload_sha256 TEXT NULL
                        CHECK(
                            raw_payload_sha256 IS NULL OR (
                                length(raw_payload_sha256)=64
                                AND raw_payload_sha256 NOT GLOB '*[^0-9a-f]*'
                            )
                        )
                    """),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Unsupported incomplete monitor schema version 11.");
        }
    }

    private static string ReadColumnDefinition(string tableSql, string column)
    {
        var start = tableSql.IndexOf(column, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;
        var depth = 0;
        char? quote = null;
        for (var index = start; index < tableSql.Length; index++)
        {
            var character = tableSql[index];
            if (quote is not null)
            {
                if (character == quote
                    && (index + 1 >= tableSql.Length
                        || tableSql[index + 1] != quote))
                    quote = null;
                else if (character == quote)
                    index++;
                continue;
            }
            if (character is '\'' or '"' or '`')
            {
                quote = character;
                continue;
            }
            if (character == '(')
                depth++;
            else if (character == ')' && depth > 0)
                depth--;
            else if (character == ',' && depth == 0)
                return tableSql[start..index];
            else if (character == ')' && depth == 0)
                return tableSql[start..index];
        }
        return tableSql[start..];
    }

    private static string NormalizeColumnDefinition(string value) =>
        string.Concat(value.Where(static character => !char.IsWhiteSpace(character)))
            .ToLowerInvariant();

    private static void EnsureBaseTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_schema_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id > 0),
                observation_id TEXT NOT NULL UNIQUE,
                raw_record_id INTEGER NULL UNIQUE
                    CHECK(raw_record_id IS NULL OR raw_record_id > 0),
                raw_payload_sha256 TEXT NULL CHECK(
                    raw_payload_sha256 IS NULL OR (
                        length(raw_payload_sha256)=64
                        AND raw_payload_sha256 NOT GLOB '*[^0-9a-f]*'
                    )
                ),
                input_evidence_kind TEXT NULL CHECK(
                    (input_evidence_kind IS NULL AND raw_payload_sha256 IS NULL)
                    OR (input_evidence_kind='payload_sha256' AND raw_payload_sha256 IS NOT NULL)
                    OR (input_evidence_kind='deleted_before_digest_v10' AND raw_payload_sha256 IS NULL)
                ),
                ingest_batch_id TEXT NULL UNIQUE,
                source_surface TEXT NULL,
                source_application_version TEXT NULL,
                source_adapter TEXT NULL,
                adapter_version TEXT NULL,
                schema_fingerprint TEXT NULL,
                inventory_hash TEXT NULL,
                compatibility_state TEXT NOT NULL CHECK (compatibility_state IN ('supported', 'supported_with_unknown_fields', 'schema_drift_detected', 'unsupported_source_version', 'recognized_record_drop_detected', 'adapter_failure')),
                reason_code TEXT NULL CHECK (reason_code IS NULL OR reason_code IN ('unknown_fields_observed', 'unsupported_source_version', 'schema_drift_detected', 'recognized_record_drop_detected', 'adapter_parse_failure', 'adapter_exception')),
                next_action TEXT NOT NULL CHECK (next_action IN ('none', 'review_unknown_fields', 'use_compatible_source_or_update_adapter', 'capture_fixture_and_review_mapping', 'restore_mapping_or_update_versioned_golden', 'validate_payload_and_protocol', 'inspect_sanitized_adapter_failure')),
                capture_content_state TEXT NULL CHECK (capture_content_state IS NULL OR capture_content_state IN ('available', 'not_captured', 'redacted', 'unsupported')),
                unknown_span_count INTEGER NOT NULL CHECK (unknown_span_count >= 0),
                unknown_event_count INTEGER NOT NULL CHECK (unknown_event_count >= 0),
                unknown_attribute_count INTEGER NOT NULL CHECK (unknown_attribute_count >= 0),
                overflow_distinct_count INTEGER NOT NULL CHECK (overflow_distinct_count >= 0),
                overflow_occurrence_count INTEGER NOT NULL CHECK (overflow_occurrence_count >= 0),
                observed_at TEXT NOT NULL,
                CHECK (compatibility_state = 'adapter_failure' OR capture_content_state IS NOT NULL),
                CHECK (compatibility_state = 'supported' OR reason_code IS NOT NULL),
                CHECK (
                    (compatibility_state = 'supported' AND reason_code IS NULL AND next_action = 'none') OR
                    (compatibility_state = 'supported_with_unknown_fields' AND reason_code = 'unknown_fields_observed' AND next_action = 'review_unknown_fields') OR
                    (compatibility_state = 'unsupported_source_version' AND reason_code = 'unsupported_source_version' AND next_action = 'use_compatible_source_or_update_adapter') OR
                    (compatibility_state = 'schema_drift_detected' AND reason_code = 'schema_drift_detected' AND next_action = 'capture_fixture_and_review_mapping') OR
                    (compatibility_state = 'recognized_record_drop_detected' AND reason_code = 'recognized_record_drop_detected' AND next_action = 'restore_mapping_or_update_versioned_golden') OR
                    (compatibility_state = 'adapter_failure' AND reason_code = 'adapter_parse_failure' AND next_action = 'validate_payload_and_protocol') OR
                    (compatibility_state = 'adapter_failure' AND reason_code = 'adapter_exception' AND next_action = 'inspect_sanitized_adapter_failure')
                )
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_unknown_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id > 0),
                source_observation_id INTEGER NOT NULL CHECK(source_observation_id > 0),
                kind TEXT NOT NULL CHECK (kind IN ('span', 'event', 'attribute')),
                name TEXT NOT NULL,
                occurrence_count INTEGER NOT NULL CHECK (occurrence_count BETWEEN 1 AND 1000000),
                source_version_label TEXT NULL,
                first_observed_at TEXT NOT NULL,
                last_observed_at TEXT NOT NULL,
                opaque_sample_reference TEXT NOT NULL,
                UNIQUE(source_observation_id, kind, name),
                CHECK (first_observed_at <= last_observed_at)
            );
            """);
    }

    private static void EnsureDurableProjectionInputs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? predecessorVersion)
    {
        var hasEvidenceKind =
            ColumnExists(connection, transaction, "source_schema_observations", "input_evidence_kind");
        var hasPayloadDigest =
            ColumnExists(connection, transaction, "source_schema_observations", "raw_payload_sha256");
        if (ColumnExists(connection, transaction, "source_schema_observations", "raw_owner_identity")
            || hasEvidenceKind != hasPayloadDigest)
            throw new InvalidOperationException("Unsupported incomplete monitor schema version 11.");
        if (hasEvidenceKind)
            return;

        var recovered = new List<(
            long ObservationId,
            long RawRecordId,
            bool RawRowExists,
            string RawPayloadStorageClass,
            string? Payload,
            bool HasProjectionChild)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT source.id,source.raw_record_id,raw.id IS NOT NULL,
                       typeof(raw.payload_json),
                       CASE WHEN typeof(raw.payload_json)='text' THEN raw.payload_json END,
                       EXISTS(
                           SELECT 1
                           FROM source_trace_version_observations AS trace
                           WHERE trace.source_observation_id=source.id)
                FROM source_schema_observations AS source
                LEFT JOIN raw_records AS raw ON raw.id=source.raw_record_id
                WHERE source.raw_record_id IS NOT NULL
                ORDER BY source.id;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                recovered.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2) != 0,
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5) != 0));
            }
        }
        if (recovered.Any(static item =>
                item.RawRowExists
                && !string.Equals(
                    item.RawPayloadStorageClass,
                    "text",
                    StringComparison.Ordinal)))
            throw new InvalidOperationException("source_projection_input_authority_invalid");

        Execute(
            connection,
            transaction,
            """
            ALTER TABLE source_schema_observations
            ADD COLUMN raw_payload_sha256 TEXT NULL
                CHECK(
                    raw_payload_sha256 IS NULL OR (
                        length(raw_payload_sha256)=64
                        AND raw_payload_sha256 NOT GLOB '*[^0-9a-f]*'
                    )
                );
            """);
        Execute(
            connection,
            transaction,
            """
            ALTER TABLE source_schema_observations
            ADD COLUMN input_evidence_kind TEXT NULL CHECK(
                (input_evidence_kind IS NULL AND raw_payload_sha256 IS NULL)
                OR (input_evidence_kind='payload_sha256' AND raw_payload_sha256 IS NOT NULL)
                OR (input_evidence_kind='deleted_before_digest_v10' AND raw_payload_sha256 IS NULL)
            );
            """);

        foreach (var item in recovered)
        {
            var evidenceKind = item.RawRowExists
                ? "payload_sha256"
                : item.HasProjectionChild && predecessorVersion == 10
                    ? "deleted_before_digest_v10"
                    : null;
            if (item.HasProjectionChild && evidenceKind is null)
                throw new InvalidOperationException("Unsupported incomplete monitor schema version 11.");
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE source_schema_observations
                SET input_evidence_kind=$evidence_kind,
                    raw_payload_sha256=$payload_sha256
                WHERE id=$observation_id;
                """;
            update.Parameters.AddWithValue(
                "$evidence_kind",
                evidenceKind is null ? DBNull.Value : evidenceKind);
            update.Parameters.AddWithValue(
                "$payload_sha256",
                item.Payload is null
                    ? DBNull.Value
                    : SkillProjectionHashing.InputDigest(item.Payload));
            update.Parameters.AddWithValue("$observation_id", item.ObservationId);
            update.ExecuteNonQuery();
        }
    }

    private static void EnsureBaseTraceCompatibilityRevisions(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var traces = new List<(string TraceId, string UpdatedAt)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT trace.trace_id,MAX(source.observed_at)
                FROM source_trace_version_observations AS trace
                INNER JOIN source_schema_observations AS source
                  ON source.id=trace.source_observation_id
                LEFT JOIN source_trace_compatibility_revisions AS revision
                  ON revision.trace_id=trace.trace_id
                WHERE revision.trace_id IS NULL
                GROUP BY trace.trace_id
                ORDER BY trace.trace_id;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
                traces.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var trace in traces)
        {
            var effective = SourceCompatibilityReconciler.ReadEffectiveTrace(
                connection,
                transaction,
                trace.TraceId)
                ?? throw new InvalidOperationException(
                    "source_compatibility_effective_trace_invalid");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO source_trace_compatibility_revisions(
                    trace_id,current_revision,current_effective_state,
                    current_exact_version,updated_at)
                VALUES($trace_id,0,$state,$version,$updated_at);
                """;
            insert.Parameters.AddWithValue("$trace_id", trace.TraceId);
            insert.Parameters.AddWithValue(
                "$state",
                SkillProjectionGenerationParticipant.Wire(effective.State));
            insert.Parameters.AddWithValue(
                "$version",
                (object?)effective.SourceApplicationVersion ?? DBNull.Value);
            insert.Parameters.AddWithValue("$updated_at", trace.UpdatedAt);
            insert.ExecuteNonQuery();
        }
    }

    private static void EnsureRestrictedTraceVersionTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (!TableExists(connection, transaction, "source_trace_version_observations"))
        {
            CreateRestrictedTraceVersionTable(
                connection,
                transaction,
                "source_trace_version_observations");
            return;
        }
        if (HasRestrictedForeignKey(connection, transaction))
            return;

        Execute(connection, transaction, "DROP TRIGGER IF EXISTS source_trace_version_observations_update_rejected;");
        Execute(connection, transaction, "DROP TRIGGER IF EXISTS source_trace_version_observations_delete_rejected;");
        Execute(connection, transaction, "DROP TRIGGER IF EXISTS source_schema_observations_trace_version_child_delete_rejected;");
        CreateRestrictedTraceVersionTable(
            connection,
            transaction,
            "source_trace_version_observations_v11");
        Execute(
            connection,
            transaction,
            """
            INSERT INTO source_trace_version_observations_v11(
                source_observation_id,trace_id,resolution_state,source_application_version)
            SELECT source_observation_id,trace_id,resolution_state,source_application_version
            FROM source_trace_version_observations;
            """);
        Execute(connection, transaction, "DROP TABLE source_trace_version_observations;");
        Execute(
            connection,
            transaction,
            "ALTER TABLE source_trace_version_observations_v11 RENAME TO source_trace_version_observations;");
    }

    private static void CreateRestrictedTraceVersionTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName) =>
        Execute(
            connection,
            transaction,
            $"""
            CREATE TABLE {tableName} (
                source_observation_id INTEGER NOT NULL CHECK(source_observation_id > 0),
                trace_id TEXT NOT NULL,
                resolution_state TEXT NOT NULL CHECK (resolution_state IN ('resolved', 'missing', 'conflicting', 'unrecognised')),
                source_application_version TEXT NULL CHECK (source_application_version IS NULL OR (length(source_application_version) BETWEEN 1 AND 256)),
                PRIMARY KEY (source_observation_id, trace_id),
                FOREIGN KEY (source_observation_id) REFERENCES source_schema_observations(id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                CHECK (
                    (resolution_state = 'resolved' AND source_application_version IS NOT NULL) OR
                    (resolution_state IN ('missing', 'conflicting') AND source_application_version IS NULL) OR
                    resolution_state = 'unrecognised'
                )
            );
            """);

    private static void EnsureBaseIndexesAndTriggers(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_source_schema_observations_cursor ON source_schema_observations(id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_source_unknown_observations_cursor ON source_unknown_observations(source_observation_id, id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_source_trace_version_observations_trace_id ON source_trace_version_observations(trace_id, source_observation_id);");
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_schema_observations_insert_no_replace
            BEFORE INSERT ON source_schema_observations
            WHEN EXISTS(
                SELECT 1 FROM source_schema_observations
                WHERE id=NEW.id
                   OR observation_id=NEW.observation_id COLLATE BINARY
                   OR (NEW.raw_record_id IS NOT NULL AND raw_record_id=NEW.raw_record_id)
                   OR (NEW.ingest_batch_id IS NOT NULL AND ingest_batch_id=NEW.ingest_batch_id COLLATE BINARY)
            )
            BEGIN SELECT RAISE(ABORT,'source_schema_observation_no_replace'); END;
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_trace_version_observations_update_rejected
            BEFORE UPDATE ON source_trace_version_observations
            BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_immutable'); END;
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_trace_version_observations_insert_no_replace
            BEFORE INSERT ON source_trace_version_observations
            WHEN EXISTS(
                SELECT 1 FROM source_trace_version_observations
                WHERE source_observation_id=NEW.source_observation_id
                  AND trace_id=NEW.trace_id COLLATE BINARY
            )
            BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_no_replace'); END;
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_trace_version_observations_delete_rejected
            BEFORE DELETE ON source_trace_version_observations
            BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_immutable'); END;
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_schema_observations_trace_version_child_delete_rejected
            BEFORE DELETE ON source_schema_observations
            WHEN EXISTS(
                SELECT 1 FROM source_trace_version_observations
                WHERE source_observation_id=OLD.id
            )
            BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_parent_restricted'); END;
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_schema_observations_projection_input_update_rejected
            BEFORE UPDATE OF input_evidence_kind,raw_payload_sha256 ON source_schema_observations
            WHEN OLD.input_evidence_kind IS NOT NEW.input_evidence_kind
              OR OLD.raw_payload_sha256 IS NOT NEW.raw_payload_sha256
            BEGIN SELECT RAISE(ABORT,'source_projection_input_immutable'); END;
            """);
    }

    private static bool HasRestrictedForeignKey(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_list(source_trace_version_observations);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(2), "source_schema_observations", StringComparison.Ordinal)
                && string.Equals(reader.GetString(5), "RESTRICT", StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(6), "RESTRICT", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IReadOnlyDictionary<
        (string Type, string Name),
        SqliteOwnedSchemaObject> BuildExpectedOwnedObjects()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE raw_records(
                id INTEGER PRIMARY KEY,
                payload_json TEXT NOT NULL);
            """);
        Ensure(connection, transaction);
        return ReadOwnedObjects(connection, transaction);
    }

    private static IReadOnlyDictionary<
        (string Type, string Name),
        SqliteOwnedSchemaObject> ReadOwnedObjects(
        SqliteConnection connection,
        SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(
            connection,
            transaction,
            static (name, table) =>
                OwnedObjectNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                || AuthorityNamespacePrefixes.Any(prefix =>
                    name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || AuthorityTableNames.Contains(
                    table,
                    StringComparer.OrdinalIgnoreCase));

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool NullableExact(
        SqliteDataReader reader,
        int ordinal,
        string? expected) =>
        expected is null
            ? reader.IsDBNull(ordinal)
            : !reader.IsDBNull(ordinal)
              && string.Equals(
                  reader.GetString(ordinal),
                  expected,
                  StringComparison.Ordinal);

    private static bool IsLowercaseHash(string value) =>
        value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateTextColumns(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        TextRule rule)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(0);
            var valid = rule switch
            {
                TextRule.VisibleToken =>
                    value.Length > 0
                    && value.All(static character =>
                        character is >= '\u0021' and <= '\u007e'),
                TextRule.RevisionToken =>
                    SourceCompatibilityReconciliationRequest.IsRevisionToken(value),
                TextRule.CanonicalTimestamp => IsCanonicalTimestamp(value),
                _ => false,
            };
            if (!valid)
                throw new InvalidOperationException(
                    "source_compatibility_canonical_value_invalid");
        }
    }

    private static bool IsCanonicalTimestamp(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var timestamp)
        && timestamp.Offset == TimeSpan.Zero
        && string.Equals(
            value,
            timestamp.ToUniversalTime().ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private static bool Exists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS({sql.Trim().TrimEnd(';')});";
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record ReconciliationReceiptValidationRow(
        string OperationKey,
        string Fingerprint,
        long SourceObservationId,
        string TraceId,
        long ExpectedInterpretationRevision,
        long RawRecordId,
        string EvidenceKind,
        string? RawPayloadSha256,
        string ResolverRevision,
        string RegistryRevision,
        string ProjectorVersion,
        string Outcome,
        long? ResultingSupersessionId,
        long ResultingInterpretationRevision,
        long? ResultingCompatibilityRevision,
        long? ResultingGenerationId,
        long? SourceRawRecordId,
        string? SourceEvidenceKind,
        string? SourceRawPayloadSha256);

    private enum TextRule
    {
        VisibleToken,
        RevisionToken,
        CanonicalTimestamp,
    }
}

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class SkillProjectionSchemaV1
{
    internal const string ComponentName = "skill_projection";
    internal const int Version = 1;
    internal static readonly string[] TableNames =
    [
        "skill_projection_generations",
        "skill_projection_generation_inputs",
        "skill_projection_trace_heads",
        "skill_projection_queue",
        "skill_projection_operation_receipts",
        "skill_projection_invocations",
        "skill_projection_inventories",
        "skill_projection_inventory_names",
        "skill_projection_sdk_claims",
    ];

    internal static readonly string[] ObsoleteTableNames =
    [
        "monitor_skill_inventory_names",
        "monitor_skill_inventories",
        "monitor_skill_invocations",
    ];
    internal static readonly string[] ObsoleteIndexNames =
    [
        "IX_monitor_skill_invocations_trace_id",
        "IX_monitor_skill_invocations_session_id",
        "IX_monitor_skill_inventories_trace_id",
        "IX_monitor_skill_inventories_session_id",
    ];
    private static readonly string[] ObsoleteDefinitions =
    [
        """
        CREATE TABLE monitor_skill_invocations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            raw_record_id INTEGER NOT NULL,
            trace_id TEXT NOT NULL,
            span_id TEXT NULL,
            span_ordinal INTEGER NOT NULL,
            session_id TEXT NULL,
            skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
            skill_source TEXT NULL CHECK (skill_source IS NULL OR length(skill_source) BETWEEN 1 AND 256),
            invocation_trigger TEXT NULL CHECK (invocation_trigger IS NULL OR length(invocation_trigger) BETWEEN 1 AND 256),
            source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
            projected_at TEXT NOT NULL,
            UNIQUE(raw_record_id, span_ordinal),
            UNIQUE(trace_id, span_id)
        );
        """,
        """
        CREATE TABLE monitor_skill_inventories (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            raw_record_id INTEGER NOT NULL,
            trace_id TEXT NOT NULL,
            session_id TEXT NULL,
            observed_name_count INTEGER NOT NULL CHECK (observed_name_count >= 0),
            retained_name_count INTEGER NOT NULL CHECK (retained_name_count BETWEEN 0 AND 100),
            names_truncated INTEGER NOT NULL CHECK (names_truncated IN (0, 1)),
            source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
            projected_at TEXT NOT NULL,
            UNIQUE(raw_record_id, trace_id)
        );
        """,
        """
        CREATE TABLE monitor_skill_inventory_names (
            raw_record_id INTEGER NOT NULL,
            trace_id TEXT NOT NULL,
            name_ordinal INTEGER NOT NULL CHECK (name_ordinal BETWEEN 0 AND 99),
            skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
            PRIMARY KEY (raw_record_id, trace_id, name_ordinal),
            FOREIGN KEY (raw_record_id, trace_id)
                REFERENCES monitor_skill_inventories(raw_record_id, trace_id)
                ON DELETE CASCADE
        );
        """,
        "CREATE INDEX IX_monitor_skill_invocations_trace_id ON monitor_skill_invocations(trace_id, id);",
        "CREATE INDEX IX_monitor_skill_invocations_session_id ON monitor_skill_invocations(session_id, id);",
        "CREATE INDEX IX_monitor_skill_inventories_trace_id ON monitor_skill_inventories(trace_id, id);",
        "CREATE INDEX IX_monitor_skill_inventories_session_id ON monitor_skill_inventories(session_id, id);",
    ];
    private static readonly Lazy<IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>>
        ExpectedCurrentObjects = new(BuildExpectedCurrentObjects);
    private static readonly Lazy<IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>>
        ExpectedObsoleteObjects = new(BuildExpectedObsoleteObjects);

    internal static IReadOnlyList<(string Name, string Table, string Sql)> TriggerDefinitions { get; } =
        new[]
        {
            "skill_projection_operation_receipts",
            "skill_projection_invocations",
            "skill_projection_inventories",
            "skill_projection_inventory_names",
            "skill_projection_sdk_claims",
        }
        .SelectMany(static table => new[]
        {
            (
                $"{table}_update_rejected",
                table,
                $"CREATE TRIGGER {table}_update_rejected BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;"),
            (
                $"{table}_delete_rejected",
                table,
                $"CREATE TRIGGER {table}_delete_rejected BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;"),
        })
        .Concat(new[]
        {
            (
                "skill_projection_operation_receipts_insert_replacement_rejected",
                "skill_projection_operation_receipts",
                "CREATE TRIGGER skill_projection_operation_receipts_insert_replacement_rejected BEFORE INSERT ON skill_projection_operation_receipts WHEN EXISTS(SELECT 1 FROM skill_projection_operation_receipts WHERE operation_key=NEW.operation_key) BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;"),
            (
                "skill_projection_invocations_insert_replacement_rejected",
                "skill_projection_invocations",
                "CREATE TRIGGER skill_projection_invocations_insert_replacement_rejected BEFORE INSERT ON skill_projection_invocations WHEN EXISTS(SELECT 1 FROM skill_projection_invocations WHERE invocation_id=NEW.invocation_id OR (generation_id=NEW.generation_id AND source_arm=NEW.source_arm AND raw_record_id=NEW.raw_record_id AND span_ordinal=NEW.span_ordinal)) BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;"),
            (
                "skill_projection_inventories_insert_replacement_rejected",
                "skill_projection_inventories",
                "CREATE TRIGGER skill_projection_inventories_insert_replacement_rejected BEFORE INSERT ON skill_projection_inventories WHEN EXISTS(SELECT 1 FROM skill_projection_inventories WHERE inventory_id=NEW.inventory_id OR (generation_id=NEW.generation_id AND source_arm=NEW.source_arm AND raw_record_id=NEW.raw_record_id AND trace_id=NEW.trace_id)) BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;"),
            (
                "skill_projection_inventory_names_insert_replacement_rejected",
                "skill_projection_inventory_names",
                "CREATE TRIGGER skill_projection_inventory_names_insert_replacement_rejected BEFORE INSERT ON skill_projection_inventory_names WHEN EXISTS(SELECT 1 FROM skill_projection_inventory_names WHERE inventory_id=NEW.inventory_id AND name_ordinal=NEW.name_ordinal) BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;"),
            (
                "skill_projection_sdk_claims_insert_replacement_rejected",
                "skill_projection_sdk_claims",
                "CREATE TRIGGER skill_projection_sdk_claims_insert_replacement_rejected BEFORE INSERT ON skill_projection_sdk_claims WHEN EXISTS(SELECT 1 FROM skill_projection_sdk_claims WHERE claim_id=NEW.claim_id OR (session_id=NEW.session_id AND event_id=NEW.event_id) OR (source_adapter=NEW.source_adapter AND source_event_id=NEW.source_event_id)) BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;"),
        })
        .ToArray();

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        var declaredVersion = ReadDeclaredVersion(connection, transaction);
        var currentObjects = ReadCurrentOwnedObjects(connection, transaction);
        if (declaredVersion is not null || currentObjects.Count != 0)
        {
            Validate(connection, transaction);
            return;
        }
        if (HasObsoleteAuthority(connection, transaction))
            throw new InvalidOperationException("Unsupported obsolete Skill projection schema.");
        ValidateDependencies(connection, transaction);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_generations (
                generation_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(generation_id > 0),
                trace_id TEXT NOT NULL CHECK(length(trace_id)=32 AND trace_id NOT GLOB '*[^0-9a-f]*'),
                compatibility_revision INTEGER NOT NULL CHECK(compatibility_revision >= 0),
                input_frontier_sha256 TEXT NOT NULL CHECK(length(input_frontier_sha256)=64 AND input_frontier_sha256 NOT GLOB '*[^0-9a-f]*'),
                projector_version TEXT NOT NULL CHECK(length(projector_version) BETWEEN 1 AND 128),
                lifecycle TEXT NOT NULL CHECK(lifecycle IN ('pending','retry_pending','current','superseded','input_unavailable','failed_terminal')),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(trace_id, compatibility_revision, input_frontier_sha256, projector_version)
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_generation_inputs (
                generation_id INTEGER NOT NULL CHECK(generation_id > 0),
                input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0),
                source_observation_id INTEGER NOT NULL CHECK(source_observation_id > 0),
                raw_record_id INTEGER NOT NULL CHECK(raw_record_id > 0),
                input_evidence_kind TEXT NOT NULL CHECK(input_evidence_kind IN ('payload_sha256','deleted_before_digest_v10')),
                raw_payload_sha256 TEXT NULL CHECK(
                    raw_payload_sha256 IS NULL OR (
                        length(raw_payload_sha256)=64
                        AND raw_payload_sha256 NOT GLOB '*[^0-9a-f]*'
                    )
                ),
                PRIMARY KEY(generation_id, input_ordinal),
                UNIQUE(generation_id, raw_record_id, source_observation_id),
                FOREIGN KEY(generation_id) REFERENCES skill_projection_generations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
                CHECK(
                    (input_evidence_kind='payload_sha256' AND raw_payload_sha256 IS NOT NULL)
                    OR (input_evidence_kind='deleted_before_digest_v10' AND raw_payload_sha256 IS NULL)
                )
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_trace_heads (
                trace_id TEXT PRIMARY KEY CHECK(length(trace_id)=32 AND trace_id NOT GLOB '*[^0-9a-f]*'),
                desired_generation_id INTEGER NULL UNIQUE CHECK(desired_generation_id IS NULL OR desired_generation_id > 0),
                current_generation_id INTEGER NULL UNIQUE CHECK(current_generation_id IS NULL OR current_generation_id > 0),
                updated_at TEXT NOT NULL,
                FOREIGN KEY(desired_generation_id) REFERENCES skill_projection_generations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY(current_generation_id) REFERENCES skill_projection_generations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_queue (
                generation_id INTEGER PRIMARY KEY CHECK(generation_id > 0),
                trace_id TEXT NOT NULL,
                compatibility_revision INTEGER NOT NULL CHECK(compatibility_revision >= 0),
                input_frontier_sha256 TEXT NOT NULL CHECK(length(input_frontier_sha256)=64 AND input_frontier_sha256 NOT GLOB '*[^0-9a-f]*'),
                projector_version TEXT NOT NULL CHECK(length(projector_version) BETWEEN 1 AND 128),
                state TEXT NOT NULL CHECK(state IN ('pending','leased','completed','superseded','input_unavailable','failed_terminal')),
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                lease_owner TEXT NULL,
                lease_generation INTEGER NOT NULL DEFAULT 0 CHECK(lease_generation >= 0),
                lease_expires_at TEXT NULL,
                next_attempt_at TEXT NULL,
                error_code TEXT NULL CHECK(error_code IS NULL OR length(error_code) BETWEEN 1 AND 128),
                UNIQUE(trace_id, compatibility_revision, input_frontier_sha256, projector_version),
                FOREIGN KEY(generation_id) REFERENCES skill_projection_generations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_operation_receipts (
                operation_key TEXT PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
                semantic_fingerprint TEXT NOT NULL CHECK(length(semantic_fingerprint)=64 AND semantic_fingerprint NOT GLOB '*[^0-9a-f]*'),
                outcome TEXT NOT NULL CHECK(outcome IN ('changed','no_change','input_unavailable')),
                generation_id INTEGER NULL CHECK(generation_id IS NULL OR generation_id > 0),
                created_at TEXT NOT NULL,
                FOREIGN KEY(generation_id) REFERENCES skill_projection_generations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_invocations (
                invocation_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(invocation_id > 0),
                generation_id INTEGER NOT NULL CHECK(generation_id > 0),
                source_arm TEXT NOT NULL CHECK(source_arm='otel_trace_span'),
                raw_record_id INTEGER NOT NULL CHECK(raw_record_id > 0),
                trace_id TEXT NOT NULL CHECK(length(trace_id)=32 AND trace_id NOT GLOB '*[^0-9a-f]*'),
                span_id TEXT NOT NULL CHECK(length(span_id)=16 AND span_id NOT GLOB '*[^0-9a-f]*'),
                span_ordinal INTEGER NOT NULL CHECK(span_ordinal >= 0),
                session_id TEXT NULL,
                skill_name TEXT NOT NULL CHECK(length(skill_name) BETWEEN 1 AND 256),
                skill_source TEXT NULL CHECK(skill_source IS NULL OR length(skill_source) BETWEEN 1 AND 256),
                invocation_trigger TEXT NULL CHECK(invocation_trigger IS NULL OR length(invocation_trigger) BETWEEN 1 AND 256),
                source_application_version TEXT NOT NULL CHECK(length(source_application_version) BETWEEN 1 AND 128),
                projected_at TEXT NOT NULL,
                UNIQUE(generation_id, source_arm, raw_record_id, span_ordinal),
                FOREIGN KEY(generation_id) REFERENCES skill_projection_generations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_inventories (
                inventory_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(inventory_id > 0),
                generation_id INTEGER NOT NULL CHECK(generation_id > 0),
                source_arm TEXT NOT NULL CHECK(source_arm='otel_trace_span'),
                raw_record_id INTEGER NOT NULL CHECK(raw_record_id > 0),
                trace_id TEXT NOT NULL CHECK(length(trace_id)=32 AND trace_id NOT GLOB '*[^0-9a-f]*'),
                session_id TEXT NULL,
                observed_name_count INTEGER NOT NULL CHECK(observed_name_count >= 0),
                retained_name_count INTEGER NOT NULL CHECK(retained_name_count BETWEEN 0 AND 100),
                names_truncated INTEGER NOT NULL CHECK(names_truncated IN (0,1)),
                source_application_version TEXT NOT NULL CHECK(length(source_application_version) BETWEEN 1 AND 128),
                projected_at TEXT NOT NULL,
                UNIQUE(generation_id, source_arm, raw_record_id, trace_id),
                FOREIGN KEY(generation_id) REFERENCES skill_projection_generations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_inventory_names (
                inventory_id INTEGER NOT NULL CHECK(inventory_id > 0),
                name_ordinal INTEGER NOT NULL CHECK(name_ordinal BETWEEN 0 AND 99),
                skill_name TEXT NOT NULL CHECK(length(skill_name) BETWEEN 1 AND 256),
                PRIMARY KEY(inventory_id, name_ordinal),
                FOREIGN KEY(inventory_id) REFERENCES skill_projection_inventories(inventory_id) ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS skill_projection_sdk_claims (
                claim_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                event_id TEXT NOT NULL,
                source_event_id TEXT NOT NULL,
                source_adapter TEXT NOT NULL,
                source_surface TEXT NOT NULL,
                source_application_version TEXT NOT NULL,
                adapter_version TEXT NOT NULL,
                normalization_version TEXT NOT NULL,
                payload_schema TEXT NOT NULL,
                schema_fingerprint TEXT NOT NULL CHECK(length(schema_fingerprint)=64 AND schema_fingerprint NOT GLOB '*[^0-9a-f]*'),
                payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256)=64 AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                producer_trace_id TEXT NULL CHECK(producer_trace_id IS NULL OR (length(producer_trace_id)=32 AND producer_trace_id NOT GLOB '*[^0-9a-f]*')),
                producer_span_id TEXT NULL CHECK(producer_span_id IS NULL OR (length(producer_span_id)=16 AND producer_span_id NOT GLOB '*[^0-9a-f]*')),
                skill_name TEXT NOT NULL CHECK(length(skill_name) BETWEEN 1 AND 256),
                skill_source TEXT NULL CHECK(skill_source IS NULL OR length(skill_source) BETWEEN 1 AND 256),
                invocation_trigger TEXT NULL CHECK(invocation_trigger IS NULL OR length(invocation_trigger) BETWEEN 1 AND 256),
                created_at TEXT NOT NULL,
                UNIQUE(session_id, event_id),
                UNIQUE(source_adapter, source_event_id),
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY(session_id,event_id) REFERENCES session_events(session_id,event_id) ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        foreach (var trigger in TriggerDefinitions)
        {
            Execute(
                connection,
                transaction,
                trigger.Sql.Replace(
                    "CREATE TRIGGER ",
                    "CREATE TRIGGER IF NOT EXISTS ",
                    StringComparison.Ordinal));
        }
        Execute(
            connection,
            transaction,
            """
            INSERT INTO schema_version(component,version)
            VALUES('skill_projection',1)
            ON CONFLICT(component) DO UPDATE SET version=excluded.version
            WHERE schema_version.version=1;
            """);
    }

    internal static void TransitionFromObsolete(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ValidateObsoleteAuthority(connection, transaction);
        foreach (var table in ObsoleteTableNames)
            Execute(connection, transaction, $"DROP TABLE IF EXISTS {table};");
        foreach (var index in ObsoleteIndexNames)
            Execute(connection, transaction, $"DROP INDEX IF EXISTS {index};");
        Ensure(connection, transaction);
    }

    internal static void RejectCollidingAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE name COLLATE NOCASE IN ({string.Join(',', TableNames.Select(static (_, index) => $"$name{index}"))})
               OR name LIKE 'skill_projection_%';
            """;
        for (var index = 0; index < TableNames.Length; index++)
            command.Parameters.AddWithValue($"$name{index}", TableNames[index]);
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0
            || ReadDeclaredVersion(connection, transaction) is not null)
        {
            throw new InvalidOperationException("Unsupported incomplete skill_projection schema version 1.");
        }
    }

    internal static void ValidateObsoleteAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (!SqliteOwnedSchemaAuthority.Equal(
                ReadObsoleteOwnedObjects(connection, transaction),
                ExpectedObsoleteObjects.Value))
            throw new InvalidOperationException("Unsupported obsolete Skill projection schema.");
    }

    internal static bool HasObsoleteAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction) =>
        ReadObsoleteOwnedObjects(connection, transaction).Count != 0;

    internal static void RejectObsoleteAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (HasObsoleteAuthority(connection, transaction))
            throw new InvalidOperationException("Unsupported obsolete Skill projection schema.");
    }

    internal static void Validate(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (ReadDeclaredVersion(connection, transaction) != Version)
            throw new InvalidOperationException("Unsupported incomplete skill_projection schema version 1.");
        ValidateDependencies(connection, transaction);
        if (!SqliteOwnedSchemaAuthority.Equal(
                ReadCurrentOwnedObjects(connection, transaction),
                ExpectedCurrentObjects.Value))
        {
            throw new InvalidOperationException(
                "Unsupported incomplete skill_projection schema version 1.");
        }
        foreach (var table in TableNames)
        {
            if (!ObjectExists(connection, transaction, "table", table))
                throw new InvalidOperationException("Unsupported incomplete skill_projection schema version 1.");
        }
        if (HasObsoleteAuthority(connection, transaction))
            throw new InvalidOperationException("Unsupported incomplete skill_projection schema version 1.");
        foreach (var trigger in TriggerDefinitions)
        {
            if (!ObjectExists(connection, transaction, "trigger", trigger.Name))
            {
                throw new InvalidOperationException("Unsupported incomplete skill_projection schema version 1.");
            }
        }
        ValidateGeneratedIdentities(connection, transaction);
        ValidateForeignKeys(connection, transaction);
        ValidateSdkClaims(connection, transaction);
        ValidateCanonicalRows(connection, transaction);
        ValidateSanitizedSkillValues(connection, transaction);
        ValidateQueueShapes(connection, transaction);
        ValidateGenerationState(connection, transaction);
        ValidateFrontiers(connection, transaction);
        ValidateProjectionProvenance(connection, transaction);
        ValidateOperationReceipts(connection, transaction);
    }

    private static void ValidateGeneratedIdentities(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1 FROM skill_projection_generations
                WHERE generation_id<=0
                UNION ALL
                SELECT 1 FROM skill_projection_generation_inputs
                WHERE generation_id<=0 OR source_observation_id<=0 OR raw_record_id<=0
                UNION ALL
                SELECT 1 FROM skill_projection_trace_heads
                WHERE desired_generation_id<=0 OR current_generation_id<=0
                UNION ALL
                SELECT 1 FROM skill_projection_queue
                WHERE generation_id<=0
                UNION ALL
                SELECT 1 FROM skill_projection_operation_receipts
                WHERE generation_id<=0
                UNION ALL
                SELECT 1 FROM skill_projection_invocations
                WHERE invocation_id<=0 OR generation_id<=0 OR raw_record_id<=0
                UNION ALL
                SELECT 1 FROM skill_projection_inventories
                WHERE inventory_id<=0 OR generation_id<=0 OR raw_record_id<=0
                UNION ALL
                SELECT 1 FROM skill_projection_inventory_names
                WHERE inventory_id<=0
                LIMIT 1;
                """))
        {
            throw new InvalidOperationException(
                "skill_projection_generated_identity_invalid");
        }
    }

    private static void ValidateSdkClaims(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM skill_projection_sdk_claims;";
        if (Convert.ToInt64(
                count.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidOperationException(
                "skill_projection_sdk_claim_authority_unpromoted");
        }
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var expected = new Dictionary<string, HashSet<(string Parent, string OnUpdate, string OnDelete)>>(
            StringComparer.Ordinal)
        {
            ["skill_projection_generation_inputs"] =
                [("skill_projection_generations", "RESTRICT", "RESTRICT")],
            ["skill_projection_trace_heads"] =
                [("skill_projection_generations", "RESTRICT", "RESTRICT")],
            ["skill_projection_queue"] =
                [("skill_projection_generations", "RESTRICT", "RESTRICT")],
            ["skill_projection_operation_receipts"] =
                [("skill_projection_generations", "RESTRICT", "RESTRICT")],
            ["skill_projection_invocations"] =
                [("skill_projection_generations", "RESTRICT", "RESTRICT")],
            ["skill_projection_inventories"] =
                [("skill_projection_generations", "RESTRICT", "RESTRICT")],
            ["skill_projection_inventory_names"] =
                [("skill_projection_inventories", "RESTRICT", "RESTRICT")],
            ["skill_projection_sdk_claims"] =
                [
                    ("sessions", "RESTRICT", "RESTRICT"),
                    ("session_events", "RESTRICT", "RESTRICT"),
                ],
        };
        foreach (var item in expected)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA foreign_key_list({item.Key});";
            using var reader = command.ExecuteReader();
            var actual = new HashSet<(string Parent, string OnUpdate, string OnDelete)>();
            while (reader.Read())
                actual.Add((reader.GetString(2), reader.GetString(5), reader.GetString(6)));
            if (!item.Value.SetEquals(actual))
                throw new InvalidOperationException("skill_projection_foreign_key_invalid");
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
            SELECT projector_version,created_at,updated_at
            FROM skill_projection_generations;
            """,
            [TextRule.RevisionToken, TextRule.CanonicalTimestamp, TextRule.CanonicalTimestamp]);
        ValidateTextColumns(
            connection,
            transaction,
            "SELECT updated_at FROM skill_projection_trace_heads;",
            [TextRule.CanonicalTimestamp]);
        ValidateTextColumns(
            connection,
            transaction,
            """
            SELECT lease_owner,lease_expires_at,next_attempt_at,error_code
            FROM skill_projection_queue;
            """,
            [
                TextRule.NullableVisibleToken,
                TextRule.NullableCanonicalTimestamp,
                TextRule.NullableCanonicalTimestamp,
                TextRule.NullableVisibleToken,
            ]);
        ValidateTextColumns(
            connection,
            transaction,
            """
            SELECT operation_key,created_at
            FROM skill_projection_operation_receipts;
            """,
            [TextRule.VisibleToken, TextRule.CanonicalTimestamp]);
        ValidateTextColumns(
            connection,
            transaction,
            """
            SELECT source_application_version,projected_at
            FROM skill_projection_invocations
            UNION ALL
            SELECT source_application_version,projected_at
            FROM skill_projection_inventories;
            """,
            [TextRule.VisibleToken, TextRule.CanonicalTimestamp]);
    }

    private static void ValidateSanitizedSkillValues(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT skill_name,skill_source,invocation_trigger
            FROM skill_projection_invocations
            UNION ALL
            SELECT skill_name,NULL,NULL
            FROM skill_projection_inventory_names;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                if (reader.IsDBNull(ordinal))
                    continue;
                var value = reader.GetString(ordinal);
                if (!string.Equals(
                        value,
                        MeasurementSanitizer.SanitizeFreeFormName(value),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "skill_projection_sanitized_value_invalid");
                }
            }
        }
    }

    private static void ValidateQueueShapes(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT state,attempt_count,lease_owner,lease_generation,
                   lease_expires_at,next_attempt_at,error_code
            FROM skill_projection_queue;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var state = reader.GetString(0);
            var attempts = reader.GetInt64(1);
            var hasOwner = !reader.IsDBNull(2);
            var leaseGeneration = reader.GetInt64(3);
            var hasExpiry = !reader.IsDBNull(4);
            var hasNextAttempt = !reader.IsDBNull(5);
            var hasError = !reader.IsDBNull(6);
            var error = hasError ? reader.GetString(6) : null;
            var valid = attempts == leaseGeneration && (state switch
            {
                "leased" =>
                    attempts >= 1
                    && leaseGeneration >= 1
                    && hasOwner
                    && hasExpiry
                    && !hasNextAttempt
                    && !hasError,
                "pending" =>
                    !hasOwner
                    && !hasExpiry
                    && (
                        attempts == 0
                        && leaseGeneration == 0
                        && !hasNextAttempt
                        && !hasError
                        || attempts >= 1
                        && leaseGeneration >= 1
                        && hasNextAttempt == hasError
                    ),
                "completed" =>
                    attempts >= 1
                    && !hasOwner && !hasExpiry && !hasNextAttempt && !hasError,
                "superseded" =>
                    !hasOwner && !hasExpiry && !hasNextAttempt && !hasError,
                "input_unavailable" =>
                    !hasOwner
                    && !hasExpiry
                    && !hasNextAttempt
                    && hasError
                    && (
                        attempts == 0
                        && leaseGeneration == 0
                        && string.Equals(
                            error,
                            "skill_projection_input_unavailable",
                            StringComparison.Ordinal)
                        || attempts >= 1
                        && leaseGeneration >= 1
                    ),
                "failed_terminal" =>
                    attempts >= 1
                    && leaseGeneration >= 1
                    && !hasOwner
                    && !hasExpiry
                    && !hasNextAttempt
                    && hasError,
                _ => false,
            });
            if (!valid)
                throw new InvalidOperationException("skill_projection_queue_state_invalid");
        }
    }

    internal static void NormalizeRestoredLeases(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_queue
            SET state='pending',lease_owner=NULL,lease_expires_at=NULL,next_attempt_at=NULL
            WHERE state='leased';
            """);

    private static void ValidateGenerationState(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM skill_projection_queue AS queue
                LEFT JOIN skill_projection_generations AS generation
                  ON generation.generation_id=queue.generation_id
                 AND generation.trace_id=queue.trace_id
                 AND generation.compatibility_revision=queue.compatibility_revision
                 AND generation.input_frontier_sha256=queue.input_frontier_sha256
                 AND generation.projector_version=queue.projector_version
                WHERE generation.generation_id IS NULL
                   OR (generation.lifecycle='pending' AND queue.state NOT IN ('pending','leased'))
                   OR (generation.lifecycle='retry_pending' AND queue.state<>'pending')
                   OR (
                        generation.lifecycle='pending'
                        AND queue.state='pending'
                        AND (
                            queue.next_attempt_at IS NOT NULL
                            OR queue.error_code IS NOT NULL
                        )
                      )
                   OR (
                        generation.lifecycle='retry_pending'
                        AND (
                            queue.attempt_count<1
                            OR queue.lease_generation<1
                            OR queue.next_attempt_at IS NULL
                            OR queue.error_code IS NULL
                        )
                      )
                   OR (generation.lifecycle='current' AND queue.state<>'completed')
                   OR (generation.lifecycle='input_unavailable' AND queue.state<>'input_unavailable')
                   OR (generation.lifecycle='failed_terminal' AND queue.state<>'failed_terminal')
                   OR (generation.lifecycle='superseded' AND queue.state NOT IN ('superseded','completed'))
            )
            OR EXISTS(
                SELECT 1
                FROM skill_projection_trace_heads AS head
                LEFT JOIN skill_projection_generations AS desired
                  ON desired.generation_id=head.desired_generation_id
                 AND desired.trace_id=head.trace_id
                LEFT JOIN skill_projection_generations AS current
                  ON current.generation_id=head.current_generation_id
                 AND current.trace_id=head.trace_id
                 AND current.lifecycle='current'
                LEFT JOIN source_trace_compatibility_revisions AS revision
                  ON revision.trace_id=head.trace_id
                WHERE revision.trace_id IS NULL
                   OR NOT EXISTS(
                        SELECT 1
                        FROM skill_projection_generations AS generation
                        WHERE generation.trace_id=head.trace_id
                      )
                   OR revision.current_revision IS NOT (
                        SELECT MAX(generation.compatibility_revision)
                        FROM skill_projection_generations AS generation
                        WHERE generation.trace_id=head.trace_id
                      )
                   OR (
                        head.current_generation_id IS NOT NULL
                        AND head.desired_generation_id IS NOT head.current_generation_id
                      )
                   OR (
                        head.desired_generation_id IS NOT NULL
                        AND (
                            desired.generation_id IS NULL
                            OR desired.lifecycle NOT IN ('pending','retry_pending','current','input_unavailable','failed_terminal')
                            OR revision.current_revision<>desired.compatibility_revision
                            OR (
                                desired.lifecycle IN ('input_unavailable','failed_terminal')
                                AND (
                                    head.current_generation_id IS NOT NULL
                                    OR EXISTS(
                                        SELECT 1
                                        FROM skill_projection_invocations AS terminal_invocation
                                        WHERE terminal_invocation.generation_id=desired.generation_id
                                    )
                                    OR EXISTS(
                                        SELECT 1
                                        FROM skill_projection_inventories AS terminal_inventory
                                        WHERE terminal_inventory.generation_id=desired.generation_id
                                    )
                                )
                            )
                            OR (
                                revision.current_effective_state<>'resolved'
                                AND NOT (
                                    desired.lifecycle='input_unavailable'
                                    AND head.current_generation_id IS NULL
                                    AND EXISTS(
                                        SELECT 1
                                        FROM skill_projection_generation_inputs AS marker_input
                                        WHERE marker_input.generation_id=desired.generation_id
                                          AND marker_input.input_evidence_kind='deleted_before_digest_v10'
                                    )
                                    AND EXISTS(
                                        SELECT 1
                                        FROM skill_projection_queue AS marker_queue
                                        WHERE marker_queue.generation_id=desired.generation_id
                                          AND marker_queue.state='input_unavailable'
                                          AND marker_queue.attempt_count=0
                                          AND marker_queue.lease_generation=0
                                          AND marker_queue.lease_owner IS NULL
                                          AND marker_queue.lease_expires_at IS NULL
                                          AND marker_queue.next_attempt_at IS NULL
                                          AND marker_queue.error_code IS 'skill_projection_input_unavailable'
                                    )
                                    AND NOT EXISTS(
                                        SELECT 1
                                        FROM skill_projection_invocations AS marker_invocation
                                        WHERE marker_invocation.generation_id=desired.generation_id
                                    )
                                    AND NOT EXISTS(
                                        SELECT 1
                                        FROM skill_projection_inventories AS marker_inventory
                                        WHERE marker_inventory.generation_id=desired.generation_id
                                    )
                                )
                            )
                        )
                      )
                   OR (
                        head.current_generation_id IS NOT NULL
                        AND (
                            current.generation_id IS NULL
                            OR revision.trace_id IS NULL
                            OR revision.current_effective_state<>'resolved'
                            OR revision.current_revision<>current.compatibility_revision
                        )
                      )
            )
            OR EXISTS(
                SELECT 1
                FROM skill_projection_generations AS generation
                LEFT JOIN skill_projection_queue AS queue
                  ON queue.generation_id=generation.generation_id
                WHERE queue.generation_id IS NULL
            )
            OR EXISTS(
                SELECT 1
                FROM skill_projection_generations AS generation
                WHERE generation.lifecycle IN ('pending','retry_pending','input_unavailable','failed_terminal')
                  AND NOT EXISTS(
                        SELECT 1
                        FROM skill_projection_trace_heads AS head
                        WHERE head.trace_id=generation.trace_id
                          AND head.desired_generation_id=generation.generation_id
                  )
            )
            OR EXISTS(
                SELECT 1
                FROM skill_projection_generations AS generation
                WHERE generation.lifecycle='current'
                  AND NOT EXISTS(
                        SELECT 1
                        FROM skill_projection_trace_heads AS head
                        WHERE head.trace_id=generation.trace_id
                          AND head.current_generation_id=generation.generation_id
                  )
            )
            OR EXISTS(
                SELECT 1
                FROM source_trace_compatibility_revisions AS revision
                LEFT JOIN skill_projection_trace_heads AS head
                  ON head.trace_id=revision.trace_id
                WHERE (
                        head.trace_id IS NULL
                        AND (
                            revision.current_revision<>0
                            OR EXISTS(
                                SELECT 1
                                FROM skill_projection_generations AS generation
                                WHERE generation.trace_id=revision.trace_id
                            )
                        )
                      )
                   OR (
                        head.trace_id IS NOT NULL
                        AND (
                            (
                                revision.current_effective_state='resolved'
                                AND head.desired_generation_id IS NULL
                            )
                            OR revision.current_revision IS NOT (
                                SELECT MAX(generation.compatibility_revision)
                                FROM skill_projection_generations AS generation
                                WHERE generation.trace_id=revision.trace_id
                            )
                        )
                      )
            )
            OR EXISTS(
                SELECT 1
                FROM skill_projection_generations AS generation
                LEFT JOIN source_trace_compatibility_revisions AS revision
                  ON revision.trace_id=generation.trace_id
                WHERE revision.trace_id IS NULL
                   OR generation.compatibility_revision>revision.current_revision
            )
            OR EXISTS(
                SELECT 1
                FROM skill_projection_generations AS generation
                JOIN skill_projection_queue AS queue
                  ON queue.generation_id=generation.generation_id
                LEFT JOIN skill_projection_trace_heads AS head
                  ON head.trace_id=generation.trace_id
                WHERE EXISTS(
                        SELECT 1
                        FROM skill_projection_generation_inputs AS input
                        WHERE input.generation_id=generation.generation_id
                          AND input.input_evidence_kind='deleted_before_digest_v10'
                      )
                  AND (
                        NOT (
                            (
                                generation.lifecycle='input_unavailable'
                                AND queue.state='input_unavailable'
                                AND queue.attempt_count=0
                                AND queue.lease_generation=0
                                AND queue.lease_owner IS NULL
                                AND queue.lease_expires_at IS NULL
                                AND queue.next_attempt_at IS NULL
                                AND queue.error_code IS 'skill_projection_input_unavailable'
                                AND head.desired_generation_id IS generation.generation_id
                                AND head.current_generation_id IS NULL
                            )
                            OR (
                                generation.lifecycle='superseded'
                                AND queue.state='superseded'
                                AND queue.attempt_count=0
                                AND queue.lease_generation=0
                                AND queue.lease_owner IS NULL
                                AND queue.lease_expires_at IS NULL
                                AND queue.next_attempt_at IS NULL
                                AND queue.error_code IS NULL
                                AND head.desired_generation_id IS NOT NULL
                                AND head.desired_generation_id<>generation.generation_id
                                AND head.current_generation_id IS NULL
                            )
                        )
                        OR EXISTS(
                            SELECT 1 FROM skill_projection_invocations
                            WHERE skill_projection_invocations.generation_id=generation.generation_id)
                        OR EXISTS(
                            SELECT 1 FROM skill_projection_inventories
                            WHERE skill_projection_inventories.generation_id=generation.generation_id)
                      )
            )
            OR EXISTS(
                SELECT 1
                FROM skill_projection_queue AS queue
                WHERE queue.state='input_unavailable'
                  AND queue.attempt_count=0
                  AND queue.lease_generation=0
                  AND NOT EXISTS(
                        SELECT 1
                        FROM skill_projection_generation_inputs AS input
                        WHERE input.generation_id=queue.generation_id
                          AND input.input_evidence_kind='deleted_before_digest_v10'
                  )
            );
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("skill_projection_generation_state_invalid");
    }

    private static void ValidateFrontiers(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var generations = connection.CreateCommand();
        generations.Transaction = transaction;
        generations.CommandText =
            """
            SELECT generation_id,trace_id,input_frontier_sha256
            FROM skill_projection_generations
            ORDER BY generation_id;
            """;
        using var generationReader = generations.ExecuteReader();
        var expected = new List<(long Id, string TraceId, string Digest)>();
        while (generationReader.Read())
            expected.Add((
                generationReader.GetInt64(0),
                generationReader.GetString(1),
                generationReader.GetString(2)));
        foreach (var generation in expected)
        {
            using var inputs = connection.CreateCommand();
            inputs.Transaction = transaction;
            inputs.CommandText =
                """
                SELECT input_ordinal,source_observation_id,raw_record_id,
                       input_evidence_kind,raw_payload_sha256
                FROM skill_projection_generation_inputs
                WHERE generation_id=$generation_id
                ORDER BY input_ordinal;
                """;
            inputs.Parameters.AddWithValue("$generation_id", generation.Id);
            using var inputReader = inputs.ExecuteReader();
            var frontier = new List<SkillProjectionFrontierInput>();
            var ordinal = 0;
            (long RawRecordId, long SourceObservationId)? previousIdentity = null;
            while (inputReader.Read())
            {
                if (inputReader.GetInt32(0) != ordinal++)
                    throw new InvalidOperationException("skill_projection_frontier_invalid");
                var sourceObservationId = inputReader.GetInt64(1);
                var rawRecordId = inputReader.GetInt64(2);
                var identity = (rawRecordId, sourceObservationId);
                if (previousIdentity is { } previous
                    && (previous.RawRecordId > identity.rawRecordId
                        || previous.RawRecordId == identity.rawRecordId
                           && previous.SourceObservationId >= identity.sourceObservationId))
                {
                    throw new InvalidOperationException("skill_projection_frontier_invalid");
                }
                var input = new SkillProjectionFrontierInput(
                    sourceObservationId,
                    rawRecordId,
                    SkillProjectionHashing.ParseEvidenceKind(inputReader.GetString(3)),
                    inputReader.IsDBNull(4) ? null : inputReader.GetString(4));
                SkillProjectionHashing.ValidateInput(input);
                frontier.Add(input);
                previousIdentity = identity;
            }
            inputReader.Close();
            if (frontier.Count == 0
                || !string.Equals(
                    SkillProjectionHashing.FrontierDigest(generation.TraceId, frontier),
                    generation.Digest,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("skill_projection_frontier_invalid");
            foreach (var input in frontier)
                ValidateFrontierAuthority(
                    connection,
                    transaction,
                    generation.Id,
                    input);
        }
        ValidateDesiredFrontierAuthority(connection, transaction);
    }

    private static void ValidateDesiredFrontierAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM skill_projection_trace_heads AS head
                JOIN skill_projection_generations AS generation
                  ON generation.generation_id=head.desired_generation_id
                 AND generation.trace_id=head.trace_id
                JOIN source_trace_version_observations AS observation
                  ON observation.trace_id=head.trace_id
                JOIN source_schema_observations AS source
                  ON source.id=observation.source_observation_id
                WHERE NOT EXISTS(
                    SELECT 1
                    FROM skill_projection_generation_inputs AS input
                    WHERE input.generation_id=generation.generation_id
                      AND input.source_observation_id=source.id
                      AND input.raw_record_id IS source.raw_record_id
                      AND input.input_evidence_kind=source.input_evidence_kind COLLATE BINARY
                      AND input.raw_payload_sha256 IS source.raw_payload_sha256 COLLATE BINARY
                );
                """))
        {
            throw new InvalidOperationException(
                "skill_projection_frontier_authority_invalid");
        }
    }

    private static void ValidateFrontierAuthority(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long generationId,
        SkillProjectionFrontierInput input)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM skill_projection_generations AS generation
            JOIN source_trace_version_observations AS observation
              ON observation.trace_id=generation.trace_id
             AND observation.source_observation_id=$source_observation_id
            JOIN source_schema_observations AS source
              ON source.id=observation.source_observation_id
             AND source.raw_record_id=$raw_record_id
             AND source.input_evidence_kind=$input_evidence_kind COLLATE BINARY
             AND source.raw_payload_sha256 IS $raw_payload_sha256 COLLATE BINARY
            LEFT JOIN retention_items AS item
              ON item.store_kind='raw_record'
             AND item.source_item_id=CAST(source.raw_record_id AS TEXT) COLLATE BINARY
            WHERE generation.generation_id=$generation_id
              AND (
                    source.input_evidence_kind='payload_sha256'
                    AND item.item_id IS NOT NULL
                 OR source.input_evidence_kind='deleted_before_digest_v10'
              );
            """;
        command.Parameters.AddWithValue("$generation_id", generationId);
        command.Parameters.AddWithValue("$source_observation_id", input.SourceObservationId);
        command.Parameters.AddWithValue("$raw_record_id", input.RawRecordId);
        command.Parameters.AddWithValue(
            "$input_evidence_kind",
            SkillProjectionHashing.Wire(input.EvidenceKind));
        command.Parameters.AddWithValue(
            "$raw_payload_sha256",
            input.RawPayloadSha256 is null ? DBNull.Value : input.RawPayloadSha256);
        if (Convert.ToInt64(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("skill_projection_frontier_authority_invalid");
    }

    private static void ValidateProjectionProvenance(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM skill_projection_generations AS generation
                LEFT JOIN skill_projection_queue AS queue
                  ON queue.generation_id=generation.generation_id
                WHERE (
                        EXISTS(
                            SELECT 1
                            FROM skill_projection_invocations AS invocation
                            WHERE invocation.generation_id=generation.generation_id
                        )
                        OR EXISTS(
                            SELECT 1
                            FROM skill_projection_inventories AS inventory
                            WHERE inventory.generation_id=generation.generation_id
                        )
                      )
                  AND (
                        generation.lifecycle NOT IN ('current','superseded')
                        OR queue.state IS NOT 'completed'
                      );
                """)
            || Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM skill_projection_invocations AS invocation
                LEFT JOIN skill_projection_generations AS generation
                  ON generation.generation_id=invocation.generation_id
                 AND generation.trace_id=invocation.trace_id
                LEFT JOIN skill_projection_generation_inputs AS input
                  ON input.generation_id=invocation.generation_id
                 AND input.raw_record_id=invocation.raw_record_id
                LEFT JOIN source_trace_compatibility_revisions AS revision
                  ON revision.trace_id=invocation.trace_id
                WHERE generation.generation_id IS NULL
                   OR input.generation_id IS NULL
                   OR (
                        generation.lifecycle='current'
                        AND (
                            revision.trace_id IS NULL
                            OR revision.current_revision<>generation.compatibility_revision
                            OR revision.current_effective_state<>'resolved'
                            OR revision.current_exact_version<>invocation.source_application_version COLLATE BINARY
                        )
                   );
                """)
            || Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM skill_projection_inventories AS inventory
                LEFT JOIN skill_projection_generations AS generation
                  ON generation.generation_id=inventory.generation_id
                 AND generation.trace_id=inventory.trace_id
                LEFT JOIN skill_projection_generation_inputs AS input
                  ON input.generation_id=inventory.generation_id
                 AND input.raw_record_id=inventory.raw_record_id
                LEFT JOIN source_trace_compatibility_revisions AS revision
                  ON revision.trace_id=inventory.trace_id
                WHERE generation.generation_id IS NULL
                   OR input.generation_id IS NULL
                   OR (
                        generation.lifecycle='current'
                        AND (
                            revision.trace_id IS NULL
                            OR revision.current_revision<>generation.compatibility_revision
                            OR revision.current_effective_state<>'resolved'
                            OR revision.current_exact_version<>inventory.source_application_version COLLATE BINARY
                        )
                   )
                   OR inventory.retained_name_count<>(
                        SELECT COUNT(*)
                        FROM skill_projection_inventory_names AS name
                        WHERE name.inventory_id=inventory.inventory_id
                   )
                   OR (
                        inventory.names_truncated=0
                        AND inventory.observed_name_count<>inventory.retained_name_count
                   )
                   OR (
                        inventory.names_truncated=1
                        AND inventory.observed_name_count<=inventory.retained_name_count
                   );
                """)
            || Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM skill_projection_inventory_names AS name
                WHERE name.name_ordinal<>(
                    SELECT COUNT(*)
                    FROM skill_projection_inventory_names AS previous
                    WHERE previous.inventory_id=name.inventory_id
                      AND previous.name_ordinal<name.name_ordinal
                );
                """))
        {
            throw new InvalidOperationException(
                "skill_projection_claim_provenance_invalid");
        }
    }

    private static void ValidateOperationReceipts(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM source_compatibility_reconciliation_receipts AS source
                LEFT JOIN skill_projection_operation_receipts AS skill
                  ON skill.operation_key=source.operation_key COLLATE BINARY
                LEFT JOIN source_trace_version_interpretation_supersessions AS supersession
                  ON supersession.supersession_id=source.resulting_supersession_id
                 AND supersession.source_observation_id=source.source_observation_id
                 AND supersession.trace_id=source.trace_id
                LEFT JOIN skill_projection_generations AS generation
                  ON generation.generation_id=source.resulting_generation_id
                 AND generation.trace_id=source.trace_id
                 AND generation.compatibility_revision=source.resulting_compatibility_revision
                LEFT JOIN source_trace_compatibility_revisions AS trace_revision
                  ON trace_revision.trace_id=source.trace_id
                WHERE skill.operation_key IS NULL
                   OR skill.semantic_fingerprint<>source.request_fingerprint COLLATE BINARY
                   OR skill.outcome<>source.outcome COLLATE BINARY
                   OR skill.generation_id IS NOT source.resulting_generation_id
                   OR skill.created_at<>source.created_at COLLATE BINARY
                   OR (
                        source.outcome='changed'
                        AND (
                            source.resulting_supersession_id IS NULL
                            OR supersession.supersession_id IS NULL
                            OR supersession.previous_interpretation_revision
                               <>source.expected_interpretation_revision
                            OR supersession.new_interpretation_revision
                               <>source.resulting_interpretation_revision
                            OR supersession.operation_fingerprint
                               <>source.request_fingerprint COLLATE BINARY
                            OR source.resulting_compatibility_revision IS NULL
                            OR source.resulting_generation_id IS NULL
                            OR generation.generation_id IS NULL
                        )
                   )
                   OR (
                        source.outcome='no_change'
                        AND (
                            source.resulting_supersession_id IS NOT NULL
                            OR source.resulting_interpretation_revision
                               <>source.expected_interpretation_revision
                            OR source.resulting_compatibility_revision IS NULL
                            OR trace_revision.trace_id IS NULL
                            OR source.resulting_compatibility_revision
                               >trace_revision.current_revision
                            OR source.resulting_generation_id IS NOT NULL
                        )
                   );
                """)
            || Exists(
                connection,
                transaction,
                """
                SELECT 1
                FROM skill_projection_operation_receipts AS skill
                LEFT JOIN source_compatibility_reconciliation_receipts AS source
                  ON source.operation_key=skill.operation_key COLLATE BINARY
                WHERE source.operation_key IS NULL;
                """))
        {
            throw new InvalidOperationException(
                "skill_projection_operation_receipt_invalid");
        }
    }

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        BuildExpectedCurrentObjects()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE schema_version(
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL);
            INSERT INTO schema_version(component,version) VALUES('monitor',11);
            CREATE TABLE retention_component_versions(
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL);
            INSERT INTO retention_component_versions(component,version)
            VALUES('retention',1);
            """);
        Ensure(connection, transaction);
        return ReadCurrentOwnedObjects(connection, transaction);
    }

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        BuildExpectedObsoleteObjects()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var definition in ObsoleteDefinitions)
            Execute(connection, transaction, definition);
        return ReadObsoleteOwnedObjects(connection, transaction);
    }

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        ReadCurrentOwnedObjects(
            SqliteConnection connection,
            SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(
            connection,
            transaction,
            static (name, table) =>
                name.StartsWith("skill_projection_", StringComparison.OrdinalIgnoreCase)
                || table.StartsWith(
                    "skill_projection_",
                    StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        ReadObsoleteOwnedObjects(
            SqliteConnection connection,
            SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(
            connection,
            transaction,
            static (name, table) =>
                name.StartsWith("monitor_skill_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("IX_monitor_skill_", StringComparison.OrdinalIgnoreCase)
                || table.StartsWith(
                    "monitor_skill_",
                    StringComparison.OrdinalIgnoreCase));

    private static void ValidateTextColumns(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        IReadOnlyList<TextRule> rules)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (var ordinal = 0; ordinal < rules.Count; ordinal++)
            {
                var isNull = reader.IsDBNull(ordinal);
                var valid = rules[ordinal] switch
                {
                    TextRule.VisibleToken =>
                        !isNull && IsVisibleToken(reader.GetString(ordinal)),
                    TextRule.RevisionToken =>
                        !isNull
                        && SourceCompatibilityReconciliationRequest
                            .IsRevisionToken(reader.GetString(ordinal)),
                    TextRule.NullableVisibleToken =>
                        isNull || IsVisibleToken(reader.GetString(ordinal)),
                    TextRule.CanonicalTimestamp =>
                        !isNull && IsCanonicalTimestamp(reader.GetString(ordinal)),
                    TextRule.NullableCanonicalTimestamp =>
                        isNull || IsCanonicalTimestamp(reader.GetString(ordinal)),
                    _ => false,
                };
                if (!valid)
                    throw new InvalidOperationException(
                        "skill_projection_canonical_value_invalid");
            }
        }
    }

    private static void ValidateDependencies(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "schema_version")
            || !ObjectExists(
                connection,
                transaction,
                "table",
                "retention_component_versions"))
        {
            throw new InvalidOperationException(
                "skill_projection_component_dependency_invalid");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                (SELECT version FROM schema_version WHERE component='monitor'),
                (SELECT version
                 FROM retention_component_versions
                 WHERE component='retention');
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.IsDBNull(0)
            || reader.GetInt64(0) != MonitorSchemaMigrator.BaseSchemaVersion
            || reader.IsDBNull(1)
            || reader.GetInt64(1) != Retention.RetentionV1Constants.CatalogSchemaVersion)
        {
            throw new InvalidOperationException(
                "skill_projection_component_dependency_invalid");
        }
    }

    private static bool IsVisibleToken(string value) =>
        value.Length > 0
        && value.All(static character => character is >= '\u0021' and <= '\u007e');

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
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar() is not null;
    }

    private static long? ReadDeclaredVersion(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "schema_version"))
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT version FROM schema_version WHERE component='skill_projection';";
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ObjectExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string type,
        string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type=$type AND name=$name);";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
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

    private enum TextRule
    {
        VisibleToken,
        RevisionToken,
        NullableVisibleToken,
        CanonicalTimestamp,
        NullableCanonicalTimestamp,
    }
}

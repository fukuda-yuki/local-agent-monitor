using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionMigrationTests
{
    private const string TraceId = "11111111111111111111111111111111";
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DirectEnsureRejectsMissingRetentionDependencyWithoutCreatingAuthority()
    {
        using var database = new TestDatabase();
        using var connection = Open(database.Path);
        Execute(
            connection,
            """
            CREATE TABLE schema_version(
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL);
            INSERT INTO schema_version(component,version) VALUES('monitor',11);
            """);
        using var transaction = connection.BeginTransaction();

        var error = Assert.Throws<InvalidOperationException>(
            () => SkillProjectionSchemaV1.Ensure(connection, transaction));
        transaction.Rollback();

        Assert.Equal("skill_projection_component_dependency_invalid", error.Message);
        Assert.Equal(
            0,
            ScalarLong(
                connection,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE name LIKE 'skill_projection_%';
                """));
    }

    [Fact]
    public void ExistingCurrentComponentRequiresExactDependenciesWithoutMutation()
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        using (var connection = Open(database.Path))
        {
            Execute(
                connection,
                """
                DELETE FROM retention_component_versions
                WHERE component='retention';
                PRAGMA journal_mode=DELETE;
                """);
        }
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        using (var connection = Open(database.Path))
        {
            Assert.Throws<InvalidOperationException>(
                () => SkillProjectionSchemaV1.Validate(connection, transaction: null));
            using var transaction = connection.BeginTransaction();
            Assert.Throws<InvalidOperationException>(
                () => SkillProjectionSchemaV1.Ensure(connection, transaction));
        }

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
    }

    [Fact]
    public void MonitorOnlyMigrationDoesNotInstallSkillBeforeRetention()
    {
        using var database = new TestDatabase();
        using (var connection = Open(database.Path))
        using (var transaction = connection.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
            transaction.Commit();
        }
        using (var connection = Open(database.Path))
        {
            Assert.Equal(
                0,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM schema_version WHERE component='skill_projection';"));
        }

        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);

        using var verification = Open(database.Path);
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                "SELECT version FROM schema_version WHERE component='skill_projection';"));
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                """
                SELECT version
                FROM retention_component_versions
                WHERE component='retention';
                """));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("extra-column")]
    [InlineData("same-name-different-index")]
    [InlineData("off-prefix-obsolete-index")]
    [InlineData("preexisting-durable-columns")]
    [InlineData("legacy-deleted-projection-input")]
    [InlineData("legacy-deleted-nonprojection-observation")]
    public void V10Transition_AcceptsOnlyExactObsoleteSkillAuthority(string shape)
    {
        var supported = shape is
            "exact" or
            "legacy-deleted-projection-input" or
            "legacy-deleted-nonprojection-observation";
        using var database = new TestDatabase();
        new RawTelemetryStore(database.Path).CreateMonitorSchema();
        using (var connection = Open(database.Path))
        {
            RemoveV11Authorities(connection);
            Execute(
                connection,
                """
                CREATE TABLE monitor_skill_invocations(
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
                CREATE TABLE monitor_skill_inventories(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    raw_record_id INTEGER NOT NULL,
                    trace_id TEXT NOT NULL,
                    session_id TEXT NULL,
                    observed_name_count INTEGER NOT NULL CHECK (observed_name_count >= 0),
                    retained_name_count INTEGER NOT NULL CHECK (retained_name_count BETWEEN 0 AND 100),
                    names_truncated INTEGER NOT NULL CHECK (names_truncated IN (0, 1)),
                    source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
                    projected_at TEXT NOT NULL,
                    UNIQUE(raw_record_id,trace_id)
                );
                CREATE TABLE monitor_skill_inventory_names(
                    raw_record_id INTEGER NOT NULL,
                    trace_id TEXT NOT NULL,
                    name_ordinal INTEGER NOT NULL CHECK (name_ordinal BETWEEN 0 AND 99),
                    skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
                    PRIMARY KEY(raw_record_id,trace_id,name_ordinal),
                    FOREIGN KEY(raw_record_id,trace_id)
                        REFERENCES monitor_skill_inventories(raw_record_id,trace_id)
                        ON DELETE CASCADE
                );
                CREATE INDEX IX_monitor_skill_invocations_trace_id
                    ON monitor_skill_invocations(trace_id,id);
                CREATE INDEX IX_monitor_skill_invocations_session_id
                    ON monitor_skill_invocations(session_id,id);
                CREATE INDEX IX_monitor_skill_inventories_trace_id
                    ON monitor_skill_inventories(trace_id,id);
                CREATE INDEX IX_monitor_skill_inventories_session_id
                    ON monitor_skill_inventories(session_id,id);
                INSERT INTO monitor_skill_invocations
                VALUES(
                    1,7,'11111111111111111111111111111111',NULL,0,NULL,
                    'old-skill-state',NULL,NULL,'1.0.0',
                    '2026-07-31T00:00:00.0000000+00:00');
                INSERT INTO monitor_skill_inventories
                VALUES(
                    1,7,'11111111111111111111111111111111',NULL,1,1,0,'1.0.0',
                    '2026-07-31T00:00:00.0000000+00:00');
                INSERT INTO monitor_skill_inventory_names
                VALUES(7,'11111111111111111111111111111111',0,'old-skill-state');
                INSERT INTO raw_records(
                    id,source,trace_id,received_at,resource_attributes_json,payload_json,
                    schema_version,retention_owner_token)
                VALUES(
                    7,'raw-otlp',NULL,'2026-07-31T00:00:00.0000000+00:00',
                    NULL,'{}',1,randomblob(32));
                INSERT INTO monitor_ingestions(
                    raw_record_id,received_at,source,trace_id,client_kind,span_count,
                    projected_at,span_projected_at)
                VALUES(
                    7,'2026-07-31T00:00:00.0000000+00:00','raw-otlp',NULL,NULL,0,
                    '2026-07-31T00:00:00.0000000+00:00',
                    '2026-07-31T00:00:01.0000000+00:00');
                UPDATE schema_version SET version=10 WHERE component='monitor';
                """);
            if (shape == "extra-column")
            {
                Execute(
                    connection,
                    "ALTER TABLE monitor_skill_invocations ADD COLUMN unknown_authority TEXT NULL;");
            }
            else if (shape == "same-name-different-index")
            {
                Execute(
                    connection,
                    """
                    DROP INDEX IX_monitor_skill_invocations_trace_id;
                    CREATE INDEX IX_monitor_skill_invocations_trace_id
                    ON monitor_skill_invocations(trace_id);
                    """);
            }
            else if (shape == "off-prefix-obsolete-index")
            {
                Execute(
                    connection,
                    """
                    CREATE INDEX unexpected_skill_authority
                    ON monitor_skill_invocations(raw_record_id);
                    """);
            }
            else if (shape == "preexisting-durable-columns")
            {
                Execute(
                    connection,
                    """
                    ALTER TABLE source_schema_observations
                    ADD COLUMN input_evidence_kind TEXT NULL;
                    ALTER TABLE source_schema_observations
                    ADD COLUMN raw_payload_sha256 BLOB NULL;
                    """);
            }
            else if (shape == "legacy-deleted-projection-input")
            {
                Execute(
                    connection,
                    """
                    INSERT INTO source_schema_observations(
                        observation_id,raw_record_id,ingest_batch_id,source_surface,
                        source_application_version,source_adapter,adapter_version,
                        schema_fingerprint,inventory_hash,compatibility_state,reason_code,
                        next_action,capture_content_state,unknown_span_count,
                        unknown_event_count,unknown_attribute_count,
                        overflow_distinct_count,overflow_occurrence_count,observed_at)
                    VALUES(
                        'legacy-deleted-observation',7,'legacy-deleted-batch',
                        'github-copilot-cli','1.0.74','github-copilot-otel','adapter-1',
                        NULL,NULL,'supported',NULL,'none','available',0,0,0,0,0,
                        '2026-07-31T00:00:00.0000000+00:00');
                    INSERT INTO source_trace_version_observations(
                        source_observation_id,trace_id,resolution_state,
                        source_application_version)
                    VALUES(
                        1,'11111111111111111111111111111111','resolved','1.0.74');
                    DELETE FROM raw_records WHERE id=7;
                    """);
            }
            else if (shape == "legacy-deleted-nonprojection-observation")
            {
                Execute(
                    connection,
                    """
                    INSERT INTO source_schema_observations(
                        observation_id,raw_record_id,ingest_batch_id,source_surface,
                        source_application_version,source_adapter,adapter_version,
                        schema_fingerprint,inventory_hash,compatibility_state,reason_code,
                        next_action,capture_content_state,unknown_span_count,
                        unknown_event_count,unknown_attribute_count,
                        overflow_distinct_count,overflow_occurrence_count,observed_at)
                    VALUES(
                        'legacy-deleted-nonprojection',7,'legacy-nonprojection-batch',
                        'github-copilot-cli','1.0.74','github-copilot-otel','adapter-1',
                        NULL,NULL,'supported',NULL,'none','available',0,0,0,0,0,
                        '2026-07-31T00:00:00.0000000+00:00');
                    DELETE FROM raw_records WHERE id=7;
                    """);
            }
            if (!supported)
                Execute(connection, "PRAGMA journal_mode=DELETE;");
        }

        if (!supported)
        {
            var before = SHA256.HashData(File.ReadAllBytes(database.Path));
            Assert.Throws<InvalidOperationException>(
                new RawTelemetryStore(database.Path).CreateMonitorSchema);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
            return;
        }

        new RawTelemetryStore(database.Path).CreateMonitorSchema();

        using var verification = Open(database.Path);
        Assert.Equal(11, ScalarLong(verification, "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal(1, ScalarLong(verification, "SELECT version FROM schema_version WHERE component='skill_projection';"));
        Assert.Equal(0, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'monitor_skill_%';"));
        Assert.Equal(
            shape == "exact" ? 1 : 0,
            ScalarLong(
                verification,
                "SELECT COUNT(*) FROM raw_records WHERE id=7;"));
        if (shape == "legacy-deleted-nonprojection-observation")
        {
            Assert.Equal(
                "legacy-deleted-nonprojection",
                ScalarText(
                    verification,
                    "SELECT observation_id FROM source_schema_observations;"));
            Assert.Equal(0, ScalarLong(
                verification,
                "SELECT COUNT(*) FROM source_trace_version_observations;"));
        }
        if (shape == "legacy-deleted-projection-input")
        {
            Assert.Equal(
                "deleted_before_digest_v10",
                ScalarText(
                    verification,
                    "SELECT input_evidence_kind FROM source_schema_observations WHERE id=1;"));
            Assert.Equal(
                1,
                ScalarLong(
                    verification,
                    "SELECT raw_payload_sha256 IS NULL FROM source_schema_observations WHERE id=1;"));
            Assert.Equal(
                0,
                ScalarLong(
                    verification,
                    "SELECT COUNT(*) FROM raw_records WHERE id=7;"));
            Assert.Equal(
                1,
                ScalarLong(
                    verification,
                    "SELECT COUNT(*) FROM source_trace_version_observations WHERE source_observation_id=1;"));
        }
        Assert.Equal(
            "2026-07-31T00:00:01.0000000+00:00",
            ScalarText(verification, "SELECT span_projected_at FROM monitor_ingestions WHERE raw_record_id=7;"));
        Assert.Equal(0, ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_generations;"));
        Assert.Equal(
            11,
            MonitorSchemaMigrator.ValidateBeforeInitialization(verification));
    }

    [Fact]
    public void V10Transition_WithRetainedProjectionInput_SeedsSourceRevisionAndKeepsSkillEmptyAcrossRestore()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(CreateBatch());
        using (var connection = Open(database.Path))
        {
            RemoveV11Authorities(connection);
            CreateExactObsoleteSkillAuthority(connection, committed.RawRecordId);
            Execute(
                connection,
                """
                UPDATE schema_version SET version=10 WHERE component='monitor';
                """);
        }

        new RawTelemetryStore(database.Path).CreateMonitorSchema();
        new RawTelemetryStore(database.Path).CreateMonitorSchema();

        using (var verification = Open(database.Path))
        {
            SourceCompatibilitySchemaV11.Validate(verification, transaction: null);
            SkillProjectionSchemaV1.Validate(verification, transaction: null);
            Assert.Equal(1, ScalarLong(
                verification,
                $"SELECT COUNT(*) FROM raw_records WHERE id={committed.RawRecordId};"));
            Assert.Equal(1, ScalarLong(
                verification,
                $"SELECT COUNT(*) FROM source_schema_observations WHERE id={committed.ObservationId};"));
            Assert.Equal(1, ScalarLong(
                verification,
                $"SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId}';"));
            Assert.Equal(0, ScalarLong(
                verification,
                $"SELECT current_revision FROM source_trace_compatibility_revisions WHERE trace_id='{TraceId}';"));
            Assert.Equal(0, ScalarLong(
                verification,
                "SELECT COUNT(*) FROM skill_projection_generations;"));
            Assert.Equal(0, ScalarLong(
                verification,
                "SELECT COUNT(*) FROM skill_projection_trace_heads;"));
            Assert.Equal(0, ScalarLong(
                verification,
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'monitor_skill_%';"));
            Assert.Equal(
                "payload_sha256",
                ScalarText(
                    verification,
                    $"SELECT input_evidence_kind FROM source_schema_observations WHERE id={committed.ObservationId};"));
            Assert.Equal(
                SkillProjectionHashing.InputDigest("{}"),
                ScalarText(
                    verification,
                    $"SELECT raw_payload_sha256 FROM source_schema_observations WHERE id={committed.ObservationId};"));
            Assert.Equal(
                0,
                ScalarLong(
                    verification,
                    "SELECT COUNT(*) FROM pragma_table_info('source_schema_observations') WHERE name='raw_owner_identity';"));
        }

        var bundle = Path.Combine(database.Root, "retained-v10.zip");
        var restoredPath = Path.Combine(database.Root, "retained-v10-restored.sqlite");
        var service = new SqliteRuntimeBackupService();
        Assert.True(service.CreateAndPublish(database.Path, bundle).Success);
        var restored = service.Restore(
            bundle,
            restoredPath,
            new RuntimeRestoreOptions());
        Assert.True(restored.Success, restored.ErrorCode);

        new RawTelemetryStore(restoredPath).CreateMonitorSchema();
        new RawTelemetryStore(restoredPath).CreateMonitorSchema();
        using var restoredVerification = Open(restoredPath);
        SourceCompatibilitySchemaV11.Validate(restoredVerification, transaction: null);
        SkillProjectionSchemaV1.Validate(restoredVerification, transaction: null);
        Assert.Equal(1, ScalarLong(
            restoredVerification,
            $"SELECT COUNT(*) FROM raw_records WHERE id={committed.RawRecordId};"));
        Assert.Equal(1, ScalarLong(
            restoredVerification,
            $"SELECT COUNT(*) FROM source_schema_observations WHERE id={committed.ObservationId};"));
        Assert.Equal(1, ScalarLong(
            restoredVerification,
            $"SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId}';"));
        Assert.Equal(0, ScalarLong(
            restoredVerification,
            $"SELECT current_revision FROM source_trace_compatibility_revisions WHERE trace_id='{TraceId}';"));
        Assert.Equal(0, ScalarLong(
            restoredVerification,
            "SELECT COUNT(*) FROM skill_projection_generations;"));
        Assert.Equal(0, ScalarLong(
            restoredVerification,
            "SELECT COUNT(*) FROM skill_projection_trace_heads;"));
        Assert.Equal(0, ScalarLong(
            restoredVerification,
            "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'monitor_skill_%';"));
        Assert.Equal(
            "payload_sha256",
            ScalarText(
                restoredVerification,
                $"SELECT input_evidence_kind FROM source_schema_observations WHERE id={committed.ObservationId};"));
    }

    [Fact]
    public void CurrentMonitorValidationRejectsTraceChildAttachedToLegacyNullAuthority()
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        using var connection = Open(database.Path);
        Execute(
            connection,
            """
            INSERT INTO source_schema_observations(
                observation_id,raw_record_id,input_evidence_kind,raw_payload_sha256,
                ingest_batch_id,source_surface,source_application_version,source_adapter,
                adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                reason_code,next_action,capture_content_state,unknown_span_count,
                unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                overflow_occurrence_count,observed_at)
            VALUES(
                'forged-legacy-child',7,NULL,NULL,'forged-legacy-child-batch',
                'github-copilot-cli','1.0.74','github-copilot-otel','adapter-1',
                NULL,NULL,'supported',NULL,'none','available',0,0,0,0,0,
                '2026-07-31T00:00:00.0000000+00:00');
            INSERT INTO source_trace_version_observations(
                source_observation_id,trace_id,resolution_state,
                source_application_version)
            VALUES(
                1,'11111111111111111111111111111111','resolved','1.0.74');
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => MonitorSchemaMigrator.ValidateBeforeInitialization(connection));

        Assert.Equal("source_projection_input_authority_invalid", error.Message);
    }

    [Fact]
    public void CurrentMonitorValidationRejectsProjectionDigestThatDoesNotMatchRawPayload()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path).Commit(CreateBatch());
        var trigger = Assert.Single(
            SourceCompatibilitySchemaV11.TriggerDefinitions,
            static item =>
                item.Name
                == "source_schema_observations_projection_input_update_rejected");
        using var connection = Open(database.Path);
        Execute(
            connection,
            $"""
            DROP TRIGGER {trigger.Name};
            UPDATE source_schema_observations
            SET raw_payload_sha256='{new string('b', 64)}';
            {trigger.Sql};
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => MonitorSchemaMigrator.ValidateBeforeInitialization(connection));

        Assert.Equal("source_projection_input_authority_invalid", error.Message);
    }

    [Fact]
    public void V10WithUnknownIntermediateSkillAuthority_FailsBeforeMutation()
    {
        using var database = new TestDatabase();
        new RawTelemetryStore(database.Path).CreateMonitorSchema();
        using (var connection = Open(database.Path))
        {
            RemoveV11Authorities(connection);
            Execute(
                connection,
                """
                UPDATE schema_version SET version=10 WHERE component='monitor';
                CREATE TABLE skill_projection_generations(id INTEGER PRIMARY KEY);
                CREATE TABLE transition_sentinel(id INTEGER PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO transition_sentinel VALUES(1,'unchanged');
                PRAGMA journal_mode=DELETE;
                """);
        }
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        Assert.Throws<InvalidOperationException>(
            new RawTelemetryStore(database.Path).CreateMonitorSchema);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
        using var verification = Open(database.Path);
        Assert.Equal(10, ScalarLong(verification, "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal("unchanged", ScalarText(verification, "SELECT value FROM transition_sentinel;"));
    }

    [Theory]
    [InlineData("extra-column")]
    [InlineData("altered-trigger")]
    [InlineData("mixed-case-extra-object")]
    [InlineData("altered-literal-case")]
    [InlineData("residual-obsolete-table")]
    [InlineData("obsolete-index-on-current-table")]
    [InlineData("off-prefix-current-index")]
    public void CurrentSchemaValidation_RejectsAnyNonCanonicalOwnedObject(
        string corruption)
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        using var connection = Open(database.Path);
        switch (corruption)
        {
            case "extra-column":
                Execute(
                    connection,
                    "ALTER TABLE skill_projection_queue ADD COLUMN unknown_authority TEXT NULL;");
                break;
            case "altered-trigger":
                Execute(
                    connection,
                    """
                    DROP TRIGGER skill_projection_invocations_update_rejected;
                    CREATE TRIGGER skill_projection_invocations_update_rejected
                    BEFORE UPDATE ON skill_projection_invocations
                    BEGIN SELECT RAISE(ABORT,'different_authority'); END;
                    """);
                break;
            case "mixed-case-extra-object":
                Execute(
                    connection,
                    "CREATE TABLE Skill_Projection_Unknown(id INTEGER PRIMARY KEY);");
                break;
            case "altered-literal-case":
                Execute(
                    connection,
                    """
                    PRAGMA writable_schema=ON;
                    UPDATE sqlite_schema
                    SET sql=replace(
                        sql,
                        'source_arm=''otel_trace_span''',
                        'source_arm=''OTEL_TRACE_SPAN''')
                    WHERE type='table'
                      AND name='skill_projection_invocations';
                    PRAGMA writable_schema=OFF;
                    PRAGMA schema_version=991;
                    """);
                break;
            case "residual-obsolete-table":
                Execute(
                    connection,
                    "CREATE TABLE MoNiToR_SkIlL_ShAdOw(id INTEGER PRIMARY KEY);");
                break;
            case "obsolete-index-on-current-table":
                Execute(
                    connection,
                    """
                    CREATE INDEX IX_Monitor_Skill_Invocations_Trace_Id
                    ON skill_projection_invocations(trace_id);
                    """);
                break;
            case "off-prefix-current-index":
                Execute(
                    connection,
                    """
                    CREATE INDEX unrelated_authority
                    ON skill_projection_invocations(trace_id);
                    """);
                break;
        }

        Assert.Throws<InvalidOperationException>(
            () => SkillProjectionSchemaV1.Validate(connection, transaction: null));
    }

    [Theory]
    [InlineData("operation-receipt-primary")]
    [InlineData("invocation-primary")]
    [InlineData("invocation-natural")]
    [InlineData("inventory-primary")]
    [InlineData("inventory-natural")]
    [InlineData("inventory-name-primary")]
    [InlineData("sdk-claim-primary")]
    [InlineData("sdk-session-event")]
    [InlineData("sdk-source-event")]
    public void AppendOnlySkillRows_RejectInsertOrReplaceWithRecursiveTriggersDisabled(
        string identity)
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        using var connection = Open(database.Path);
        Execute(
            connection,
            "PRAGMA foreign_keys=OFF; PRAGMA recursive_triggers=OFF;");
        var attack = InsertOrReplaceAttack(identity);
        Execute(connection, attack.Seed);
        var before = ReadEveryPersistedFieldAndCount(connection, attack.Table);
        Assert.Equal(1, before.Count);

        var error = Assert.Throws<SqliteException>(
            () => Execute(connection, attack.Replacement));

        Assert.Contains("skill_projection_append_only", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, ReadEveryPersistedFieldAndCount(connection, attack.Table));
    }

    [Theory]
    [InlineData("skill_projection_invocations", "skill_name", "whitespace")]
    [InlineData("skill_projection_invocations", "skill_name", "windows-path")]
    [InlineData("skill_projection_invocations", "skill_source", "email")]
    [InlineData("skill_projection_invocations", "invocation_trigger", "credential")]
    [InlineData("skill_projection_inventory_names", "skill_name", "prompt")]
    [InlineData("skill_projection_inventory_names", "skill_name", "unix-path")]
    [InlineData("skill_projection_inventory_names", "skill_name", "truncated")]
    public async Task RestoreValidation_RejectsNonCanonicalSanitizedOtelSkillValue(
        string table,
        string column,
        string corruption)
    {
        using var database = new TestDatabase();
        await SeedPublishedSkillProjection(database.Path);
        using (var connection = Open(database.Path))
        {
            var updateTrigger = Assert.Single(
                SkillProjectionSchemaV1.TriggerDefinitions,
                trigger => trigger.Table == table
                    && trigger.Name.EndsWith("_update_rejected", StringComparison.Ordinal));
            Execute(connection, $"DROP TRIGGER {updateTrigger.Name};");
            using (var mutation = connection.CreateCommand())
            {
                mutation.CommandText =
                    $"PRAGMA ignore_check_constraints=ON; UPDATE {table} SET {column}=$value;";
                mutation.Parameters.AddWithValue("$value", SanitizerCorruption(corruption));
                mutation.ExecuteNonQuery();
            }
            Execute(connection, updateTrigger.Sql);

            var error = Assert.Throws<InvalidOperationException>(
                () => SkillProjectionSchemaV1.Validate(connection, transaction: null));

            Assert.Equal("skill_projection_sanitized_value_invalid", error.Message);
            Execute(
                connection,
                "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
        }

        var preflight = new SqliteRuntimeBackupService()
            .PreflightForMigration(database.Path);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
    }

    [Theory]
    [InlineData("generation-primary")]
    [InlineData("generation-input-reference")]
    [InlineData("trace-head-desired-reference")]
    [InlineData("trace-head-current-reference")]
    [InlineData("queue-primary-reference")]
    [InlineData("operation-receipt-reference")]
    [InlineData("invocation-primary")]
    [InlineData("invocation-generation-reference")]
    [InlineData("inventory-primary")]
    [InlineData("inventory-generation-reference")]
    [InlineData("inventory-name-reference")]
    public void CurrentSchemaChecks_RejectNegativeGeneratedIdentity(string identity)
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        using var connection = Open(database.Path);
        Execute(connection, "PRAGMA foreign_keys=OFF;");

        Assert.Throws<SqliteException>(
            () => Execute(connection, NegativeIdentityInsert(identity)));
    }

    [Fact]
    public async Task RestoreValidation_RejectsCoherentNegativeGeneratedIdentityGraph()
    {
        using var database = new TestDatabase();
        await SeedPublishedSkillProjection(database.Path);
        using (var connection = Open(database.Path))
        {
            var affectedTables = new HashSet<string>(StringComparer.Ordinal)
            {
                "skill_projection_invocations",
                "skill_projection_inventories",
                "skill_projection_inventory_names",
            };
            var updateTriggers = SkillProjectionSchemaV1.TriggerDefinitions
                .Where(trigger => affectedTables.Contains(trigger.Table)
                    && trigger.Name.EndsWith("_update_rejected", StringComparison.Ordinal))
                .ToArray();
            foreach (var trigger in updateTriggers)
                Execute(connection, $"DROP TRIGGER {trigger.Name};");
            Execute(
                connection,
                $"""
                PRAGMA foreign_keys=OFF;
                PRAGMA ignore_check_constraints=ON;
                UPDATE skill_projection_inventory_names
                SET inventory_id=-inventory_id;
                UPDATE skill_projection_inventories
                SET inventory_id=-inventory_id,generation_id=-generation_id;
                UPDATE skill_projection_invocations
                SET invocation_id=-invocation_id,generation_id=-generation_id;
                UPDATE skill_projection_generation_inputs
                SET generation_id=-generation_id;
                UPDATE skill_projection_queue
                SET generation_id=-generation_id;
                UPDATE skill_projection_trace_heads
                SET desired_generation_id=CASE
                        WHEN desired_generation_id IS NULL THEN NULL
                        ELSE -desired_generation_id
                    END,
                    current_generation_id=CASE
                        WHEN current_generation_id IS NULL THEN NULL
                        ELSE -current_generation_id
                    END;
                UPDATE skill_projection_generations
                SET generation_id=-generation_id;
                INSERT INTO skill_projection_operation_receipts
                VALUES(
                    'negative-generation-proof','{new string('a', 64)}','changed',-1,
                    '2026-07-31T00:00:00.0000000+00:00');
                """);
            foreach (var trigger in updateTriggers)
                Execute(connection, trigger.Sql);

            var error = Assert.Throws<InvalidOperationException>(
                () => SkillProjectionSchemaV1.Validate(connection, transaction: null));

            Assert.Equal("skill_projection_generated_identity_invalid", error.Message);
            Execute(
                connection,
                "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
        }

        var preflight = new SqliteRuntimeBackupService()
            .PreflightForMigration(database.Path);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
    }

    [Fact]
    public void CurrentMonitorValidationRejectsAlteredDurableColumnDefinition()
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        using var connection = Open(database.Path);
        Execute(
            connection,
            """
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema
            SET sql=replace(
                sql,
                'input_evidence_kind TEXT NULL',
                'input_evidence_kind BLOB NULL')
            WHERE type='table'
              AND name='source_schema_observations';
            PRAGMA writable_schema=OFF;
            PRAGMA schema_version=992;
            """);

        Assert.Throws<InvalidOperationException>(
            () => MonitorSchemaMigrator.ValidateBeforeInitialization(connection));
    }

    [Theory]
    [InlineData("extra-column")]
    [InlineData("altered-constraint")]
    [InlineData("wrong-object-type")]
    [InlineData("wrong-table-owner")]
    [InlineData("same-name-altered-trigger")]
    [InlineData("off-prefix-attached-index")]
    public void CurrentSourceValidation_RejectsNonCanonicalOwnedAuthorityBeforeMutation(
        string corruption)
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        using (var connection = Open(database.Path))
        {
            switch (corruption)
            {
                case "extra-column":
                    Execute(
                        connection,
                        """
                        ALTER TABLE source_trace_compatibility_revisions
                        ADD COLUMN unknown_authority TEXT NULL;
                        """);
                    break;
                case "altered-constraint":
                    Execute(
                        connection,
                        """
                        PRAGMA writable_schema=ON;
                        UPDATE sqlite_schema
                        SET sql=replace(
                            sql,
                            'CHECK(current_revision >= 0)',
                            'CHECK(current_revision >= -1)')
                        WHERE type='table'
                          AND name='source_trace_compatibility_revisions';
                        PRAGMA writable_schema=OFF;
                        PRAGMA schema_version=993;
                        """);
                    break;
                case "wrong-object-type":
                    Execute(
                        connection,
                        """
                        DROP TABLE source_trace_compatibility_revisions;
                        CREATE VIEW source_trace_compatibility_revisions AS
                        SELECT
                            CAST(NULL AS TEXT) AS trace_id,
                            CAST(NULL AS INTEGER) AS current_revision,
                            CAST(NULL AS TEXT) AS current_effective_state,
                            CAST(NULL AS TEXT) AS current_exact_version,
                            CAST(NULL AS TEXT) AS updated_at
                        WHERE 0;
                        """);
                    break;
                case "wrong-table-owner":
                    Execute(
                        connection,
                        """
                        PRAGMA writable_schema=ON;
                        UPDATE sqlite_schema
                        SET tbl_name='source_trace_compatibility_revisions'
                        WHERE type='trigger'
                          AND name='source_compatibility_reconciliation_receipts_update_rejected';
                        PRAGMA writable_schema=OFF;
                        PRAGMA schema_version=994;
                        """);
                    break;
                case "same-name-altered-trigger":
                    Execute(
                        connection,
                        """
                        DROP TRIGGER source_compatibility_reconciliation_receipts_update_rejected;
                        CREATE TRIGGER source_compatibility_reconciliation_receipts_update_rejected
                        BEFORE UPDATE ON source_compatibility_reconciliation_receipts
                        BEGIN SELECT RAISE(ABORT,'different_authority'); END;
                        """);
                    break;
                case "off-prefix-attached-index":
                    Execute(
                        connection,
                        """
                        CREATE INDEX unexpected_source_authority
                        ON source_trace_compatibility_revisions(current_revision);
                        """);
                    break;
            }
            Execute(
                connection,
                """
                PRAGMA wal_checkpoint(TRUNCATE);
                PRAGMA journal_mode=DELETE;
                """);
        }
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        using (var validation = Open(database.Path))
        {
            Assert.ThrowsAny<Exception>(
                () => SourceCompatibilitySchemaV11.Validate(
                    validation,
                    transaction: null));
        }
        var preflight = new SqliteRuntimeBackupService()
            .PreflightForMigration(database.Path);

        Assert.False(preflight.Success);
        Assert.Equal(
            RuntimeBackupErrorCodes.RestoreIncompatible,
            preflight.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
    }

    [Fact]
    public void Restore_NormalizesLeasedSkillWorkBackToPending()
    {
        using var database = new TestDatabase();
        var lease = SeedLeasedGeneration(database.Path);
        var bundle = Path.Combine(database.Root, "backup.zip");
        var restoredPath = Path.Combine(database.Root, "restored.sqlite");
        var service = new SqliteRuntimeBackupService();
        Assert.True(service.CreateAndPublish(database.Path, bundle).Success);

        var restored = service.Restore(bundle, restoredPath, new RuntimeRestoreOptions());

        Assert.True(restored.Success, restored.ErrorCode);
        using var verification = Open(restoredPath);
        Assert.Equal("pending", ScalarText(verification, "SELECT state FROM skill_projection_queue;"));
        Assert.Equal(0, ScalarLong(
            verification,
            """
            SELECT COUNT(*)
            FROM skill_projection_queue
            WHERE lease_owner IS NOT NULL
               OR lease_expires_at IS NOT NULL
               OR next_attempt_at IS NOT NULL;
            """));
        Assert.Equal(lease.AttemptCount, ScalarLong(
            verification,
            "SELECT attempt_count FROM skill_projection_queue;"));
        Assert.Equal(lease.LeaseGeneration, ScalarLong(
            verification,
            "SELECT lease_generation FROM skill_projection_queue;"));
    }

    [Fact]
    public void RestorePreflightRejectsMalformedLeasedSkillRowBeforeNormalization()
    {
        using var database = new TestDatabase();
        _ = SeedLeasedGeneration(database.Path);
        using (var connection = Open(database.Path))
        {
            Execute(
                connection,
                """
                UPDATE skill_projection_queue
                SET next_attempt_at='2026-08-01T00:00:00.0000000+00:00'
                WHERE state='leased';
                PRAGMA wal_checkpoint(TRUNCATE);
                """);
        }

        var result = new SqliteRuntimeBackupService()
            .PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        using var verification = Open(database.Path);
        Assert.Equal("leased", ScalarText(
            verification,
            "SELECT state FROM skill_projection_queue;"));
        Assert.Equal(
            "2026-08-01T00:00:00.0000000+00:00",
            ScalarText(
                verification,
                "SELECT next_attempt_at FROM skill_projection_queue;"));
    }

    [Theory]
    [InlineData("missing-queue")]
    [InlineData("source-identity")]
    [InlineData("revision-mismatch")]
    [InlineData("split-head")]
    [InlineData("orphan-pending")]
    public void CurrentSchemaValidation_RejectsBrokenGenerationAuthority(string corruption)
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        const string traceId = "11111111111111111111111111111111";
        var frontier = SkillProjectionHashing.FrontierDigest(
            traceId,
            [new SkillProjectionFrontierInput(
                1,
                7,
                SkillProjectionInputEvidenceKind.PayloadSha256,
                new string('a', 64))]);
        using var connection = Open(database.Path);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO source_trace_compatibility_revisions(
                    trace_id,current_revision,current_effective_state,current_exact_version,updated_at)
                VALUES(
                    $trace_id,$current_revision,'resolved','1.0.74',
                    '2026-07-31T00:00:00.0000000+00:00');
                INSERT INTO skill_projection_generations(
                    trace_id,compatibility_revision,input_frontier_sha256,projector_version,
                    lifecycle,created_at,updated_at)
                VALUES(
                    $trace_id,0,$frontier,'skill-projector-1','pending',
                    '2026-07-31T00:00:00.0000000+00:00',
                    '2026-07-31T00:00:00.0000000+00:00');
                INSERT INTO skill_projection_generation_inputs(
                    generation_id,input_ordinal,source_observation_id,raw_record_id,
                    input_evidence_kind,raw_payload_sha256)
                VALUES(1,0,$source_observation_id,7,'payload_sha256',$input_sha256);
                INSERT INTO skill_projection_trace_heads(
                    trace_id,desired_generation_id,current_generation_id,updated_at)
                VALUES(
                    $trace_id,1,NULL,
                    '2026-07-31T00:00:00.0000000+00:00');
                INSERT INTO skill_projection_queue(
                    generation_id,trace_id,compatibility_revision,input_frontier_sha256,
                    projector_version,state)
                VALUES(1,$trace_id,0,$frontier,'skill-projector-1','pending');
                """;
            command.Parameters.AddWithValue("$trace_id", traceId);
            command.Parameters.AddWithValue(
                "$current_revision",
                corruption == "revision-mismatch" ? 1 : 0);
            command.Parameters.AddWithValue("$frontier", frontier);
            command.Parameters.AddWithValue(
                "$source_observation_id",
                corruption == "source-identity" ? 8 : 1);
            command.Parameters.AddWithValue("$input_sha256", new string('a', 64));
            command.ExecuteNonQuery();
        }
        if (corruption == "missing-queue")
            Execute(connection, "DELETE FROM skill_projection_queue;");
        else if (corruption == "orphan-pending")
            Execute(
                connection,
                "UPDATE skill_projection_trace_heads SET desired_generation_id=NULL;");
        else if (corruption == "split-head")
        {
            using var split = connection.CreateCommand();
            split.CommandText =
                """
                UPDATE skill_projection_generations
                SET lifecycle='current'
                WHERE generation_id=1;
                UPDATE skill_projection_queue
                SET state='completed'
                WHERE generation_id=1;
                INSERT INTO skill_projection_generations(
                    trace_id,compatibility_revision,input_frontier_sha256,projector_version,
                    lifecycle,created_at,updated_at)
                VALUES(
                    $trace_id,0,$frontier,'skill-projector-2','pending',
                    '2026-07-31T00:00:00.0000000+00:00',
                    '2026-07-31T00:00:00.0000000+00:00');
                INSERT INTO skill_projection_generation_inputs(
                    generation_id,input_ordinal,source_observation_id,raw_record_id,
                    input_evidence_kind,raw_payload_sha256)
                VALUES(2,0,1,7,'payload_sha256',$input_sha256);
                INSERT INTO skill_projection_queue(
                    generation_id,trace_id,compatibility_revision,input_frontier_sha256,
                    projector_version,state)
                VALUES(2,$trace_id,0,$frontier,'skill-projector-2','pending');
                UPDATE skill_projection_trace_heads
                SET desired_generation_id=2,current_generation_id=1
                WHERE trace_id=$trace_id;
                """;
            split.Parameters.AddWithValue("$trace_id", traceId);
            split.Parameters.AddWithValue("$frontier", frontier);
            split.Parameters.AddWithValue("$input_sha256", new string('a', 64));
            split.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(
            () => SkillProjectionSchemaV1.Validate(connection, transaction: null));
    }

    private static void RemoveV11Authorities(SqliteConnection connection)
    {
        Execute(
            connection,
            """
            DROP TABLE skill_projection_inventory_names;
            DROP TABLE skill_projection_inventories;
            DROP TABLE skill_projection_invocations;
            DROP TABLE skill_projection_sdk_claims;
            DROP TABLE skill_projection_operation_receipts;
            DROP TABLE skill_projection_queue;
            DROP TABLE skill_projection_trace_heads;
            DROP TABLE skill_projection_generation_inputs;
            DROP TABLE skill_projection_generations;
            DELETE FROM schema_version WHERE component='skill_projection';
            DROP TABLE source_compatibility_reconciliation_receipts;
            DROP TABLE source_trace_version_interpretation_heads;
            DROP TABLE source_trace_version_interpretation_supersessions;
            DROP TABLE source_trace_compatibility_revisions;
            DROP TRIGGER IF EXISTS source_schema_observations_insert_no_replace;
            DROP TRIGGER IF EXISTS source_trace_version_observations_insert_no_replace;
            DROP TRIGGER source_trace_version_observations_update_rejected;
            DROP TRIGGER source_trace_version_observations_delete_rejected;
            DROP TRIGGER source_schema_observations_trace_version_child_delete_rejected;
            DROP TRIGGER source_schema_observations_projection_input_update_rejected;
            ALTER TABLE source_schema_observations
            DROP COLUMN input_evidence_kind;
            ALTER TABLE source_schema_observations
            DROP COLUMN raw_payload_sha256;
            """);
    }

    private static (string Table, string Seed, string Replacement) InsertOrReplaceAttack(
        string identity)
    {
        const string at = "2026-07-31T00:00:00.0000000+00:00";
        const string trace = "11111111111111111111111111111111";
        const string otherTrace = "22222222222222222222222222222222";
        const string span = "3333333333333333";
        var invocation =
            $"(1,1,'otel_trace_span',7,'{trace}','{span}',0,NULL,'safe-skill',NULL,NULL,'1.0.74','{at}')";
        var inventory =
            $"(1,1,'otel_trace_span',7,'{trace}',NULL,1,1,0,'1.0.74','{at}')";
        var sdk =
            $"('claim-1','session-1','event-1','source-event-1','adapter-1','copilot-sdk','1.0.0','adapter-v1','normalizer-v1','skill-invocation-v1','{new string('a', 64)}','{new string('b', 64)}',NULL,NULL,'safe-skill',NULL,NULL,'{at}')";
        return identity switch
        {
            "operation-receipt-primary" =>
                ("skill_projection_operation_receipts",
                 $"INSERT INTO skill_projection_operation_receipts VALUES('operation-1','{new string('a', 64)}','no_change',NULL,'{at}');",
                 $"INSERT OR REPLACE INTO skill_projection_operation_receipts VALUES('operation-1','{new string('b', 64)}','no_change',NULL,'{at}');"),
            "invocation-primary" =>
                ("skill_projection_invocations",
                 $"INSERT INTO skill_projection_invocations VALUES{invocation};",
                 $"INSERT OR REPLACE INTO skill_projection_invocations VALUES(1,1,'otel_trace_span',8,'{otherTrace}','4444444444444444',1,NULL,'other-skill',NULL,NULL,'1.0.74','{at}');"),
            "invocation-natural" =>
                ("skill_projection_invocations",
                 $"INSERT INTO skill_projection_invocations VALUES{invocation};",
                 $"INSERT OR REPLACE INTO skill_projection_invocations VALUES(2,1,'otel_trace_span',7,'{trace}','4444444444444444',0,NULL,'other-skill',NULL,NULL,'1.0.74','{at}');"),
            "inventory-primary" =>
                ("skill_projection_inventories",
                 $"INSERT INTO skill_projection_inventories VALUES{inventory};",
                 $"INSERT OR REPLACE INTO skill_projection_inventories VALUES(1,1,'otel_trace_span',8,'{otherTrace}',NULL,1,1,0,'1.0.74','{at}');"),
            "inventory-natural" =>
                ("skill_projection_inventories",
                 $"INSERT INTO skill_projection_inventories VALUES{inventory};",
                 $"INSERT OR REPLACE INTO skill_projection_inventories VALUES(2,1,'otel_trace_span',7,'{trace}',NULL,2,1,1,'1.0.74','{at}');"),
            "inventory-name-primary" =>
                ("skill_projection_inventory_names",
                 "INSERT INTO skill_projection_inventory_names VALUES(1,0,'safe-skill');",
                 "INSERT OR REPLACE INTO skill_projection_inventory_names VALUES(1,0,'other-skill');"),
            "sdk-claim-primary" =>
                ("skill_projection_sdk_claims",
                 $"INSERT INTO skill_projection_sdk_claims VALUES{sdk};",
                 $"INSERT OR REPLACE INTO skill_projection_sdk_claims VALUES('claim-1','session-2','event-2','source-event-2','adapter-2','copilot-sdk','1.0.0','adapter-v1','normalizer-v1','skill-invocation-v1','{new string('a', 64)}','{new string('b', 64)}',NULL,NULL,'other-skill',NULL,NULL,'{at}');"),
            "sdk-session-event" =>
                ("skill_projection_sdk_claims",
                 $"INSERT INTO skill_projection_sdk_claims VALUES{sdk};",
                 $"INSERT OR REPLACE INTO skill_projection_sdk_claims VALUES('claim-2','session-1','event-1','source-event-2','adapter-2','copilot-sdk','1.0.0','adapter-v1','normalizer-v1','skill-invocation-v1','{new string('a', 64)}','{new string('b', 64)}',NULL,NULL,'other-skill',NULL,NULL,'{at}');"),
            "sdk-source-event" =>
                ("skill_projection_sdk_claims",
                 $"INSERT INTO skill_projection_sdk_claims VALUES{sdk};",
                 $"INSERT OR REPLACE INTO skill_projection_sdk_claims VALUES('claim-2','session-2','event-2','source-event-1','adapter-1','copilot-sdk','1.0.0','adapter-v1','normalizer-v1','skill-invocation-v1','{new string('a', 64)}','{new string('b', 64)}',NULL,NULL,'other-skill',NULL,NULL,'{at}');"),
            _ => throw new ArgumentOutOfRangeException(nameof(identity)),
        };
    }

    private static PersistedRows ReadEveryPersistedFieldAndCount(
        SqliteConnection connection,
        string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                values[ordinal] = reader.IsDBNull(ordinal)
                    ? null
                    : reader.GetValue(ordinal);
            }
            rows.Add(values);
        }
        return new(
            rows.Count,
            System.Text.Json.JsonSerializer.Serialize(rows));
    }

    private sealed record PersistedRows(int Count, string EveryField);

    private static string NegativeIdentityInsert(string identity)
    {
        const string at = "2026-07-31T00:00:00.0000000+00:00";
        const string trace = "11111111111111111111111111111111";
        const string span = "2222222222222222";
        var sha = new string('a', 64);
        return identity switch
        {
            "generation-primary" =>
                $"INSERT INTO skill_projection_generations VALUES(-1,'{trace}',0,'{sha}','skill-projector-1','pending','{at}','{at}');",
            "generation-input-reference" =>
                $"INSERT INTO skill_projection_generation_inputs VALUES(-1,0,1,1,'payload_sha256','{sha}');",
            "trace-head-desired-reference" =>
                $"INSERT INTO skill_projection_trace_heads VALUES('{trace}',-1,NULL,'{at}');",
            "trace-head-current-reference" =>
                $"INSERT INTO skill_projection_trace_heads VALUES('{trace}',NULL,-1,'{at}');",
            "queue-primary-reference" =>
                $"INSERT INTO skill_projection_queue(generation_id,trace_id,compatibility_revision,input_frontier_sha256,projector_version,state) VALUES(-1,'{trace}',0,'{sha}','skill-projector-1','pending');",
            "operation-receipt-reference" =>
                $"INSERT INTO skill_projection_operation_receipts VALUES('operation-1','{sha}','changed',-1,'{at}');",
            "invocation-primary" =>
                $"INSERT INTO skill_projection_invocations VALUES(-1,1,'otel_trace_span',1,'{trace}','{span}',0,NULL,'safe-skill',NULL,NULL,'1.0.74','{at}');",
            "invocation-generation-reference" =>
                $"INSERT INTO skill_projection_invocations VALUES(1,-1,'otel_trace_span',1,'{trace}','{span}',0,NULL,'safe-skill',NULL,NULL,'1.0.74','{at}');",
            "inventory-primary" =>
                $"INSERT INTO skill_projection_inventories VALUES(-1,1,'otel_trace_span',1,'{trace}',NULL,1,1,0,'1.0.74','{at}');",
            "inventory-generation-reference" =>
                $"INSERT INTO skill_projection_inventories VALUES(1,-1,'otel_trace_span',1,'{trace}',NULL,1,1,0,'1.0.74','{at}');",
            "inventory-name-reference" =>
                "INSERT INTO skill_projection_inventory_names VALUES(-1,0,'safe-skill');",
            _ => throw new ArgumentOutOfRangeException(nameof(identity)),
        };
    }

    private static void CreateExactObsoleteSkillAuthority(
        SqliteConnection connection,
        long rawRecordId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE monitor_skill_invocations(
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
            CREATE TABLE monitor_skill_inventories(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                session_id TEXT NULL,
                observed_name_count INTEGER NOT NULL CHECK (observed_name_count >= 0),
                retained_name_count INTEGER NOT NULL CHECK (retained_name_count BETWEEN 0 AND 100),
                names_truncated INTEGER NOT NULL CHECK (names_truncated IN (0, 1)),
                source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
                projected_at TEXT NOT NULL,
                UNIQUE(raw_record_id,trace_id)
            );
            CREATE TABLE monitor_skill_inventory_names(
                raw_record_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                name_ordinal INTEGER NOT NULL CHECK (name_ordinal BETWEEN 0 AND 99),
                skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
                PRIMARY KEY(raw_record_id,trace_id,name_ordinal),
                FOREIGN KEY(raw_record_id,trace_id)
                    REFERENCES monitor_skill_inventories(raw_record_id,trace_id)
                    ON DELETE CASCADE
            );
            CREATE INDEX IX_monitor_skill_invocations_trace_id
                ON monitor_skill_invocations(trace_id,id);
            CREATE INDEX IX_monitor_skill_invocations_session_id
                ON monitor_skill_invocations(session_id,id);
            CREATE INDEX IX_monitor_skill_inventories_trace_id
                ON monitor_skill_inventories(trace_id,id);
            CREATE INDEX IX_monitor_skill_inventories_session_id
                ON monitor_skill_inventories(session_id,id);
            INSERT INTO monitor_skill_invocations(
                raw_record_id,trace_id,span_id,span_ordinal,session_id,skill_name,
                skill_source,invocation_trigger,source_application_version,projected_at)
            VALUES(
                $raw_record_id,'11111111111111111111111111111111',
                '2222222222222222',0,NULL,'old-skill-state',NULL,NULL,'1.0.74',
                '2026-07-31T00:00:00.0000000+00:00');
            INSERT INTO monitor_skill_inventories(
                raw_record_id,trace_id,session_id,observed_name_count,
                retained_name_count,names_truncated,source_application_version,projected_at)
            VALUES(
                $raw_record_id,'11111111111111111111111111111111',
                NULL,1,1,0,'1.0.74','2026-07-31T00:00:00.0000000+00:00');
            INSERT INTO monitor_skill_inventory_names(
                raw_record_id,trace_id,name_ordinal,skill_name)
            VALUES(
                $raw_record_id,'11111111111111111111111111111111',0,
                'old-skill-state');
            """;
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.ExecuteNonQuery();
    }

    private static SkillProjectionQueueLease SeedLeasedGeneration(string path)
    {
        new SqliteSourceCompatibilityStore(path).CreateSchema();
        new SqliteIngestionCommitStore(path).Commit(CreateBatch());
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(path);
        var store = new SqliteSkillProjectionStore(
            path,
            new RawTelemetryStore(path, retention));
        return Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(ObservedAt.AddSeconds(1)));
    }

    private static async Task SeedPublishedSkillProjection(string path)
    {
        new SqliteSourceCompatibilityStore(path).CreateSchema();
        new SqliteIngestionCommitStore(path).Commit(CreateBatch(SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(path);
        var worker = new SkillProjectionWorker(
            new SqliteSkillProjectionStore(
                path,
                new RawTelemetryStore(path, retention)));

        Assert.Equal(
            SkillProjectionWorkOutcome.Published,
            await worker.RunNextAsync(ObservedAt.AddSeconds(1)));
    }

    private static string SanitizerCorruption(string corruption) => corruption switch
    {
        "whitespace" => "   ",
        "windows-path" => @"C:\synthetic\SKILL.md",
        "email" => "synthetic@example.test",
        "credential" => "api_key=synthetic",
        "prompt" => "prompt: synthetic",
        "unix-path" => "/tmp/synthetic-skill",
        "truncated" => new string('x', MeasurementSanitizer.MaxSanitizedNameLength + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
    };

    private static ValidatedIngestionBatch CreateBatch(string payload = "{}")
    {
        var inventory = OtlpJsonStructuralWalker.Build(payload, ObservedAt);
        var decision = SourceCompatibilityEvaluator.Assess(
            "github-copilot-cli",
            "1.0.74",
            inventory,
            observedRecognizedCount: 1,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        var observation = SourceObservationBatchDraft.Create(
            "restore-leased-generation",
            "github-copilot-cli",
            "1.0.74",
            "github-copilot-otel",
            "adapter-1",
            inventory,
            decision,
            SourceCaptureContentState.Available,
            ObservedAt,
            [
                TraceSourceVersionResolutionDraft.Create(
                    TraceId,
                    TraceSourceVersionResolutionState.Resolved,
                    "1.0.74"),
            ]);
        return ValidatedIngestionBatch.Create(
            new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                TraceId,
                ObservedAt,
                ResourceAttributesJson: null,
                PayloadJson: payload),
            observation);
    }

    private const string SkillPayload =
        """
        {"resourceSpans":[{
          "resource":{"attributes":[
            {"key":"service.version","value":{"stringValue":"1.0.74"}},
            {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
          ]},
          "scopeSpans":[{"spans":[{
            "traceId":"11111111111111111111111111111111",
            "spanId":"2222222222222222",
            "attributes":[
              {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
              {"key":"gen_ai.tool.name","value":{"stringValue":"skill"}},
              {"key":"github.copilot.skill.name","value":{"stringValue":"safe-skill"}},
              {"key":"github.copilot.skill.source","value":{"stringValue":"project"}},
              {"key":"github.copilot.skill.invocation_trigger","value":{"stringValue":"agent-invoked"}},
              {"key":"github.copilot.context.skills","value":{"arrayValue":{"values":[
                {"stringValue":"safe-skill"}
              ]}}}
            ]
          }]}]
        }]}
        """;

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"skill-migration-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }
        public string Root => directory;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

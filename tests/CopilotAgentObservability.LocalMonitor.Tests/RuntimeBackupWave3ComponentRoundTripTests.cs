using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupWave3ComponentRoundTripTests
{
    private static readonly string[] ComponentPrefixes =
    [
        "historical_instruction_analysis_",
        "historical_import_",
        "local_repositor",
        "retention_",
        "sanitized_import_",
        "session",
        "session_repository_",
        "skill_projection_",
    ];
    private static readonly HashSet<string> PopulatedPrefixes =
    [
        "historical_instruction_analysis_",
        "historical_import_",
        "local_repositor",
        "retention_",
        "sanitized_import_",
        "session",
        "session_repository_",
        "skill_projection_",
    ];

    [Fact]
    public void Backup_and_restore_preserve_non_empty_wave_3_component_data_exactly()
    {
        using var temp = new RoundTripTemp();
        temp.CreateBaseDatabase();
        temp.SeedRetentionAndSkillProjection();
        var historicalRunId = temp.SeedHistoricalInstructionAnalysis();
        temp.SeedHistoricalImportPreview();
        var sanitizedImportId = temp.SeedSanitizedImport();
        temp.SeedLocalRepositoryCatalog();
        temp.Checkpoint();
        var expected = ReadSnapshots(temp.Source);
        var service = new SqliteRuntimeBackupService(temp.Clock);

        var created = service.CreateAndPublish(temp.Source, temp.Bundle);
        var restored = service.Restore(temp.Bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        var preflight = service.PreflightForMigration(temp.Target);
        Assert.True(preflight.Success, Describe(preflight));
        Assert.Equal(1, preflight.ComponentVersions!["historical_instruction_analysis"]);
        Assert.Equal(1, preflight.ComponentVersions["historical_import"]);
        Assert.Equal(1, preflight.ComponentVersions["sanitized_import"]);
        Assert.Equal(1, preflight.ComponentVersions["local_repository_catalog"]);
        Assert.Equal(1, preflight.ComponentVersions["retention"]);
        Assert.Equal(1, preflight.ComponentVersions["skill_projection"]);
        Assert.Empty(preflight.MigrationSteps!);
        var actual = ReadSnapshots(temp.Target);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var prefix in ComponentPrefixes)
        {
            if (PopulatedPrefixes.Contains(prefix))
                Assert.True(expected[prefix].RowCounts.Values.Sum() > 0, $"{prefix} must contain owned rows.");
            Assert.Equal(expected[prefix].RowCounts, actual[prefix].RowCounts);
            Assert.Equal(expected[prefix].Digest, actual[prefix].Digest);
        }

        var analysis = new SqliteHistoricalInstructionAnalysisStoreV1(temp.Target).Get(historicalRunId);
        Assert.NotNull(analysis);
        Assert.Equal("historical-extraction-00000000000000000000000000000000", analysis.Request.ExtractionId);
        Assert.Equal("hip_runtime_backup_round_trip", Text(temp.Target,
            "SELECT preview_id FROM historical_import_previews;"));
        var sanitizedHistory = new SqliteSanitizedImportStore(temp.Target, temp.Clock).GetHistory(sanitizedImportId);
        Assert.NotNull(sanitizedHistory);
        Assert.Equal(sanitizedImportId, sanitizedHistory.ImportId);
    }

    private static IReadOnlyDictionary<string, ComponentSnapshot> ReadSnapshots(string databasePath) =>
        ComponentPrefixes.ToDictionary(prefix => prefix, prefix => ReadSnapshot(databasePath, prefix), StringComparer.Ordinal);

    private static ComponentSnapshot ReadSnapshot(string databasePath, string prefix)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText =
            "SELECT name,sql FROM sqlite_schema WHERE type='table' AND name GLOB $pattern ORDER BY name;";
        tablesCommand.Parameters.AddWithValue("$pattern", prefix + "*");
        var tables = new List<(string Name, string Sql)>();
        using (var reader = tablesCommand.ExecuteReader())
            while (reader.Read()) tables.Add((reader.GetString(0), reader.GetString(1)));

        var canonical = new MemoryStream();
        var rowCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            Append(canonical, table.Name);
            Append(canonical, table.Sql);
            var columns = ReadColumns(connection, table.Name);
            foreach (var column in columns) Append(canonical, column);
            using var command = connection.CreateCommand();
            var quotedTable = Quote(table.Name);
            command.CommandText = $"SELECT * FROM {quotedTable} ORDER BY {string.Join(',', columns.Select(Quote))};";
            using var reader = command.ExecuteReader();
            long rowCount = 0;
            while (reader.Read())
            {
                rowCount++;
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    Append(canonical, reader.GetValue(ordinal));
            }
            rowCounts.Add(table.Name, rowCount);
        }

        return new(rowCounts, Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant());
    }

    private static string[] ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }

    private static void Append(Stream destination, object value)
    {
        var (kind, bytes) = value switch
        {
            DBNull => ("null", Array.Empty<byte>()),
            byte[] blob => ("blob", blob),
            long number => ("integer", Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture))),
            double number => ("real", Encoding.UTF8.GetBytes(number.ToString("R", CultureInfo.InvariantCulture))),
            string text => ("text", Encoding.UTF8.GetBytes(text)),
            _ => throw new InvalidOperationException($"Unexpected SQLite value type {value.GetType().FullName}."),
        };
        var header = Encoding.UTF8.GetBytes($"{kind}:{bytes.Length}:");
        destination.Write(header);
        destination.Write(bytes);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Text(string databasePath, string sql)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Describe(RuntimeBackupPreflightResult result) =>
        $"{result.ErrorCode}; components={string.Join(',', result.ComponentVersions?.Select(item => $"{item.Key}:{item.Value}") ?? [])}; migrations={string.Join(',', result.MigrationSteps ?? [])}";

    private sealed class RoundTripTemp : IDisposable
    {
        internal RoundTripTemp()
        {
            Root = Path.Combine(Path.GetTempPath(), $"runtime-backup-wave3-round-trip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "source.db");
            Target = Path.Combine(Root, "restored.db");
            Bundle = Path.Combine(Root, "wave3-components.backup.zip");
            Clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 3, 4, 5, TimeSpan.Zero));
        }

        internal string Root { get; }
        internal string Source { get; }
        internal string Target { get; }
        internal string Bundle { get; }
        internal TimeProvider Clock { get; }

        internal void CreateBaseDatabase()
        {
            using var connection = Open(Source);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using (var transaction = connection.BeginTransaction())
            {
                MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                transaction.Commit();
            }
            using (var transaction = connection.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(connection, transaction);
                transaction.Commit();
            }
            new SqliteSourceCompatibilityStore(Source).CreateSchema();
            new SqliteSessionStore(Source).CreateSchema();
            using (var transaction = connection.BeginTransaction())
            {
                LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
                SkillProjectionSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
        }

        internal void SeedLocalRepositoryCatalog()
        {
            const string at = "2026-07-23T03:04:05.0000000+00:00";
            const string sessionId = "01900000-0000-7000-8000-00000000c001";
            const string eventId = "01900000-0000-7000-8000-00000000c002";
            const long rawRecordId = 7001;
            const string traceId = "11111111111111111111111111111111";
            const string spanId = "2222222222222222";
            const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            long nextId = 0xc100;
            string Id(DateTimeOffset _) => $"01900000-0000-7000-8000-{Interlocked.Increment(ref nextId):x12}";
            var queue = new SqliteLocalRepositoryReconciliationStore(Source, Clock, static () => new string('d', 64));
            var application = new LocalRepositoryCatalogApplication(new SqliteLocalRepositoryCatalogStore(
                Source,
                queue,
                new LocalRepositoryAssignmentResolver(Id),
                Clock,
                Id));
            var create = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
                application.PrepareCreate(new("Runtime Backup Catalog", "https://github.com/Synthetic/WaveThree.git"))).Prepared;
            var created = application.ExecutePreparedAsync(
                create,
                OperationKey(0x41),
                LocalRepositoryCatalogFixture.RepositoryEntity,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsType<LocalRepositoryMutationSucceeded>(created);

            using (var connection = Open(Source))
            {
                // D079 clause E keeps runtime backup validation strict: a session row whose
                // status/completeness/ended_at disagrees with the deterministic outcome
                // reducer fails backup preflight (restore_incompatible). Clause A reduces an
                // OTel-only fact set to active with null ended_at, so the completed/full
                // session must carry real terminal evidence — native binding, lifecycle
                // start, user instruction, the exact OTel span, and a clean
                // session.task_complete fact — for the reducer to derive completed/full.
                Execute(connection, $"""
                    INSERT INTO sessions(session_id,status,completeness,ended_at,last_seen_at,raw_retention_state,created_at,updated_at)
                    VALUES('{sessionId}','completed','full','{at}','{at}','not_captured','{at}','{at}');
                    INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
                    VALUES('{sessionId}','copilot-cli','wave3-native-session','native','{at}');
                    INSERT INTO session_events(
                        event_id,session_id,source_surface,trace_id,source_adapter,source_event_id,
                        type,occurred_at,content_state,source_application_version)
                    VALUES(
                        '{eventId}','{sessionId}','copilot-cli','{traceId}','otel-exact','{traceId}/{spanId}',
                        'otel.span','{at}','not_captured','1.2.3');
                    INSERT INTO session_events(
                        event_id,session_id,source_surface,source_adapter,source_event_id,
                        type,occurred_at,content_state,source_application_version)
                    VALUES(
                        '01900000-0000-7000-8000-00000000c006','{sessionId}','copilot-sdk','copilot-sdk-stream','wave3-session-start',
                        'session.start','2026-07-23T03:03:05.0000000+00:00','not_captured','1.2.3');
                    INSERT INTO session_events(
                        event_id,session_id,source_surface,source_adapter,source_event_id,
                        type,occurred_at,content_state,source_application_version)
                    VALUES(
                        '01900000-0000-7000-8000-00000000c007','{sessionId}','copilot-sdk','copilot-sdk-stream','wave3-user-message',
                        'user.message','2026-07-23T03:03:35.0000000+00:00','not_captured','1.2.3');
                    INSERT INTO session_events(
                        event_id,session_id,source_surface,source_adapter,source_event_id,
                        type,occurred_at,content_state,source_application_version,
                        terminal_outcome,terminal_policy_version)
                    VALUES(
                        '01900000-0000-7000-8000-00000000c008','{sessionId}','copilot-sdk','copilot-sdk-stream','wave3-task-complete',
                        'session.task_complete','{at}','not_captured','1.2.3','clean',1);
                    """);
            }
            var repositoryId = Text(Source, "SELECT repository_id FROM local_repositories;");
            var locatorId = Text(Source, "SELECT locator_id FROM local_repository_locators;");
            var assign = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                application.PrepareSessionAction(new(sessionId, 0, "assign", repositoryId))).Prepared;
            var assigned = application.ExecutePreparedAsync(
                assign,
                OperationKey(0x42),
                LocalRepositoryCatalogFixture.AssignmentEntity,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsType<LocalRepositoryMutationSucceeded>(assigned);

            var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
                LocalRepositorySourceIdentityInput.Span(rawRecordId, 0, 0, 0, 0, "vcs.repository.url.full"));
            var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
                sourceIdentity,
                sessionId,
                eventId,
                traceId,
                spanId));
            var reconciliationFingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
                LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, digest));
            using var database = Open(Source);
            using var command = database.CreateCommand();
            command.CommandText = $"""
                INSERT INTO source_schema_observations(
                    observation_id,raw_record_id,input_evidence_kind,raw_payload_sha256,
                    ingest_batch_id,source_surface,source_application_version,source_adapter,
                    adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                    reason_code,next_action,capture_content_state,unknown_span_count,
                    unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                    overflow_occurrence_count,observed_at)
                VALUES(
                    'wave3-source-observation',{rawRecordId},'payload_sha256','{digest}',
                    'wave3-batch','github-copilot-cli','1.2.3','raw-otlp',
                    '1','synthetic','synthetic','supported',NULL,'none','available',0,0,0,0,0,'{at}');
                INSERT INTO local_repository_reconciliation_queue(
                    queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,
                    reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,
                    terminal_reason,created_at,updated_at)
                VALUES(
                    '01900000-0000-7000-8000-00000000c003',{rawRecordId},'payload_sha256','{digest}',
                    'local-repository-catalog:1','{reconciliationFingerprint}','completed',1,NULL,NULL,NULL,'{at}','{at}');
                INSERT INTO session_repository_observations(
                    observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,
                    resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,
                    scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,
                    locator_sha256,display_owner,display_repository,source_surface,
                    source_application_version,observed_at)
                SELECT
                    '01900000-0000-7000-8000-00000000c004','{sourceIdentity}',{rawRecordId},'{digest}',
                    0,0,0,0,'span','vcs.repository.url.full','admitted',kind,canonical_locator,
                    locator_sha256,display_owner,display_repository,'github-copilot-cli','1.2.3','{at}'
                FROM local_repository_locators WHERE locator_id='{locatorId}';
                INSERT INTO session_repository_observation_contexts(
                    context_id,observation_id,context_identity_sha256,session_event_id,session_id,
                    trace_id,span_id,admission_state,repository_id,locator_id,observed_at)
                VALUES(
                    '01900000-0000-7000-8000-00000000c005',
                    '01900000-0000-7000-8000-00000000c004','{contextIdentity}','{eventId}','{sessionId}',
                    '{traceId}','{spanId}','admitted','{repositoryId}','{locatorId}','{at}');
                INSERT INTO monitor_spans(
                    raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,
                    tool_name,tool_type,mcp_tool_name,mcp_server_hash,agent_name,request_model,
                    response_model,input_tokens,output_tokens,total_tokens,reasoning_tokens,
                    cache_read_tokens,cache_creation_tokens,status,error_type,finish_reasons,
                    conversation_id,duration_ms,start_time,end_time,projected_at)
                VALUES(
                    {rawRecordId},'{traceId}',NULL,NULL,0,
                    NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                    NULL,NULL,NULL,NULL,NULL,'{at}');
                UPDATE local_repository_reconciliation_state
                SET last_discovered_span_id=(SELECT MAX(id) FROM monitor_spans),
                    updated_at='{at}'
                WHERE projector_key='local-repository-catalog-v1';
                """;
            command.ExecuteNonQuery();
        }

        internal void SeedRetentionAndSkillProjection()
        {
            const string traceId = "33333333333333333333333333333333";
            const string payload = "{}";
            var at = Clock.GetUtcNow();
            var inventory = OtlpJsonStructuralWalker.Build(payload, at);
            var decision = SourceCompatibilityEvaluator.Assess(
                "github-copilot-cli",
                "1.0.74",
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], []));
            var observation = SourceObservationBatchDraft.Create(
                "wave3-runtime-backup-ingestion",
                "github-copilot-cli",
                "1.0.74",
                "github-copilot-otel",
                "adapter-1",
                inventory,
                decision,
                SourceCaptureContentState.Available,
                at,
                [TraceSourceVersionResolutionDraft.Create(
                    traceId,
                    TraceSourceVersionResolutionState.Resolved,
                    "1.0.74")]);
            var batch = ValidatedIngestionBatch.Create(
                new RawTelemetryRecord(
                    null,
                    RawTelemetrySources.RawOtlp,
                    traceId,
                    at,
                    ResourceAttributesJson: null,
                    PayloadJson: payload),
                observation);

            _ = new SqliteIngestionCommitStore(Source).Commit(batch);

            using var connection = Open(Source);
            SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
            SkillProjectionSchemaV1.Validate(connection, transaction: null);
        }

        private static string OperationKey(byte value) =>
            "lrc1_" + Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal long SeedHistoricalInstructionAnalysis()
        {
            var store = new SqliteHistoricalInstructionAnalysisStoreV1(Source);
            store.CreateSchema();
            return store.Start(
                new(
                    HistoricalInstructionAnalysisContractsV1.RequestSchemaVersion,
                    "historical-extraction-00000000000000000000000000000000",
                    new string('a', 64),
                    "gpt-5",
                    "copilot",
                    new string('b', 64),
                    30_000,
                    HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion),
                new(
                    TruncatedBefore: false,
                    SanitizedOnly: true,
                    ContentAvailable: false,
                    new HistoricalEvidenceDistributionV1(
                        [new HistoricalDistributionCountV1("partial", 1)],
                        [new HistoricalDistributionCountV1("historical", 1)],
                        [new HistoricalDistributionCountV1("metadata_only", 1)])),
                Clock.GetUtcNow());
        }

        internal void SeedHistoricalImportPreview()
        {
            new SqliteHistoricalImportStore(Source).CreateSchema();
            using var connection = Open(Source);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO historical_import_previews(
                    preview_id,preview_digest,snapshot_version,snapshot_digest,source_selection_id,
                    private_selection_json,probe_json,candidate_batch_json,preview_json,eligible,expires_at,created_at)
                VALUES(
                    'hip_runtime_backup_round_trip',$preview_digest,'hsv_1',$snapshot_digest,'hss_runtime_backup_round_trip',
                    NULL,NULL,NULL,'{"schema_version":"historical-import-workflow-preview/v1","result":"metadata_only"}',0,
                    '2026-07-23T03:09:05.0000000+00:00','2026-07-23T03:04:05.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$preview_digest", new string('c', 64));
            command.Parameters.AddWithValue("$snapshot_digest", new string('d', 64));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal string SeedSanitizedImport()
        {
            var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
            const string recordId = "runtime-backup-synthetic-record";
            var canonicalBytes = RepositoryMetadataProjectionV1.Serialize(
                recordId, recordId, "trace-synthetic", "github-copilot-cli", "synthetic-repository",
                "synthetic-workspace", "synthetic-snapshot", observedAt, "partial", "not_captured", "retained_by_policy");
            var record = new SanitizedExportRecord(
                $"repository-metadata/{recordId}.json", "repository_metadata_projection", recordId,
                recordId, "trace-synthetic", "github-copilot-cli", "synthetic-repository", "synthetic-workspace",
                "synthetic-snapshot", observedAt, canonicalBytes, [], "partial", "not_captured", "retained_by_policy");
            var snapshot = new SanitizedExportSourceSnapshot(
                "synthetic-runtime-backup-snapshot", "local-monitor-test", [new("github-copilot-cli", "1.0.73")],
                [record], new("missing", "missing", "unavailable", "unavailable", "unavailable"));
            var archive = new SanitizedExportService().Create(new(observedAt, snapshot, new()));
            Assert.True(archive.Success, archive.ErrorCode);
            var store = new SqliteSanitizedImportStore(Source, Clock);
            store.CreateSchema();
            var preview = store.Preview(archive.ArchiveBytes!);
            Assert.True(preview.Success, preview.ErrorCode);
            var result = store.Commit(archive.ArchiveBytes!, preview.PreviewDigest!);
            Assert.True(result.Success, result.ErrorCode);
            return result.ImportId!;
        }

        internal void Checkpoint()
        {
            using var connection = Open(Source);
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record ComponentSnapshot(
        IReadOnlyDictionary<string, long> RowCounts,
        string Digest);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

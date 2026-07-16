using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.Telemetry;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Doctor.Tests.Persistence;

public sealed class DoctorMigrationTests
{
    private const string GenerationCommand = "dotnet run --project scripts/test/GenerateMonitorSchemaFixtures/GenerateMonitorSchemaFixtures.csproj -- --output tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor";

    public static TheoryData<int, string> HistoricalSchemas => new()
    {
        { 1, "655e00243df9e07b9abb3392d6e4daf747064a77" },
        { 2, "f91e195b549fa2bbfc51b3245dd3fb19fcc8759c" },
        { 3, "9ca613a97fd0611ccff1d84b35261b7346112eab" },
        { 4, "65ec872eb541b2023f55c32d32edebb9cf83818b" },
    };

    public static TheoryData<int, string, string> HistoricalSchemasAndFailurePoints
    {
        get
        {
            var data = new TheoryData<int, string, string>();
            foreach (var pair in HistoricalSchemas)
            {
                data.Add((int)pair[0], (string)pair[1], "after-doctor-tables");
                data.Add((int)pair[0], (string)pair[1], "after-doctor-version");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemas))]
    public void HistoricalMonitorFixture_MigratesThroughV5ThenDoctorV1AndRestartsIdempotently(int version, string sourceCommit)
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor");
        var manifestPath = Path.Combine(fixtureDirectory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal("monitor", manifest.Component);
        Assert.Equal(GenerationCommand, manifest.GenerationCommand);
        Assert.Equal("git status --porcelain", manifest.GitStatusCommand);
        var fixture = Assert.Single(manifest.Fixtures, item => item.Version == version);
        Assert.Equal(sourceCommit, fixture.SourceCommit);
        Assert.Equal(string.Empty, fixture.GitStatusBefore);
        Assert.Equal(string.Empty, fixture.GitStatusAfter);
        Assert.Equal($"monitor-v{version}.sqlite", fixture.File);

        var fixturePath = Path.Combine(fixtureDirectory, fixture.File);
        var sourceBytes = File.ReadAllBytes(fixturePath);
        Assert.Equal(fixture.Sha256, Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant());

        using var database = new DoctorTestDatabase();
        File.Copy(fixturePath, database.Path, overwrite: true);
        AssertHistoricalSentinels(database, version, fixture.Sentinels);

        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = new SqliteDoctorVerificationStore(database.Path, time);
        Assert.Equal(DoctorResultCode.VerificationActive, store.CreateSchema().Code);
        var verification = Assert.IsType<DoctorVerification>(
            store.Start("github-copilot-vscode", "otel", TimeSpan.FromMinutes(5)).Verification);
        Assert.Equal(
            DoctorResultCode.VerificationActive,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, $"fixture-monitor-v{version}")).Code);

        string firstRestartRows;
        using (var firstRestart = database.Open())
        {
            AssertMigratedState(firstRestart, fixture.Sentinels);
            AssertCurrentMonitorV5Schema(firstRestart);
            firstRestartRows = SnapshotDoctorRows(firstRestart);
        }

        var compatibilityStore = new SqliteSourceCompatibilityStore(database.Path);
        var compatibilityId = compatibilityStore.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
            $"doctor-migration-v{version}",
            ingestBatchId: null,
            sourceSurface: null,
            sourceApplicationVersion: null,
            sourceAdapter: null,
            adapterVersion: null,
            captureContentState: null,
            observedAt: DoctorTestData.Now));
        var compatibilityRow = Assert.Single(compatibilityStore.List(compatibilityId - 1, limit: 1));
        Assert.Equal(compatibilityId, compatibilityRow.Id);
        Assert.Equal($"doctor-migration-v{version}", compatibilityRow.ObservationId);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, compatibilityRow.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], compatibilityRow.ReasonCodes);

        compatibilityStore.CreateSchema();
        var reopenedStore = new SqliteDoctorVerificationStore(database.Path, time);
        Assert.Equal(DoctorResultCode.VerificationActive, reopenedStore.CreateSchema().Code);
        Assert.Equal(DoctorResultCode.VerificationActive, reopenedStore.Get(verification.VerificationId).Code);
        using (var secondRestart = database.Open())
        {
            AssertMigratedState(secondRestart, fixture.Sentinels);
            AssertCurrentMonitorV5Schema(secondRestart);
            Assert.Equal(firstRestartRows, SnapshotDoctorRows(secondRestart));
            Assert.Equal(
                1L,
                DoctorTestDatabase.Scalar(
                    secondRestart,
                    "SELECT count(*) FROM source_schema_observations WHERE id=$id AND observation_id=$observationId;",
                    ("$id", compatibilityId),
                    ("$observationId", $"doctor-migration-v{version}")));
        }

        Assert.Equal(sourceBytes, File.ReadAllBytes(fixturePath));
        Assert.Equal(fixture.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemasAndFailurePoints))]
    public void InjectedDoctorMigrationFailure_RestoresExactPostMonitorPreDoctorSchemaAndRows(
        int version,
        string sourceCommit,
        string failurePoint)
    {
        Assert.False(string.IsNullOrWhiteSpace(sourceCommit));
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "SchemaMigrations",
            "monitor",
            $"monitor-v{version}.sqlite");
        using var database = new DoctorTestDatabase();
        File.Copy(fixturePath, database.Path, overwrite: true);
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();

        string before;
        using (var connection = database.Open())
        {
            before = SnapshotDatabase(connection);
            Assert.Equal(5L, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='monitor';"));
            Assert.Null(DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='doctor';"));
        }

        var store = new SqliteDoctorVerificationStore(
            database.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now),
            checkpoint: point =>
            {
                if (point == failurePoint)
                {
                    throw new InvalidOperationException("injected Doctor migration failure");
                }
            });

        var result = store.CreateSchema();

        Assert.Equal(DoctorResultCode.DoctorStoreUnavailable, result.Code);
        using var reopened = database.Open();
        Assert.Equal(before, SnapshotDatabase(reopened));
        Assert.Equal(0L, DoctorTestDatabase.Scalar(reopened, "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'doctor_%';"));
        Assert.Null(DoctorTestDatabase.Scalar(reopened, "SELECT version FROM schema_version WHERE component='doctor';"));
    }

    private static void AssertHistoricalSentinels(DoctorTestDatabase database, int version, FixtureSentinels sentinels)
    {
        using var connection = database.Open();
        Assert.Equal((long)version, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal(sentinels.RawRecordId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM raw_records WHERE id=$id;", ("$id", sentinels.RawRecordId)));
        Assert.Equal(sentinels.IngestionId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_ingestions WHERE id=$id;", ("$id", sentinels.IngestionId)));
        Assert.Equal(sentinels.TraceId, DoctorTestDatabase.Scalar(connection, "SELECT trace_id FROM monitor_traces WHERE trace_id=$id;", ("$id", sentinels.TraceId)));
        Assert.Equal(sentinels.TraceRowId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_traces WHERE trace_id=$id;", ("$id", sentinels.TraceId)));
        if (sentinels.SpanId is not null)
        {
            Assert.Equal(sentinels.SpanId, DoctorTestDatabase.Scalar(connection, "SELECT span_id FROM monitor_spans WHERE span_id=$id;", ("$id", sentinels.SpanId)));
            Assert.Equal(sentinels.SpanRowId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_spans WHERE span_id=$id;", ("$id", sentinels.SpanId)));
        }
        else
        {
            Assert.Null(sentinels.SpanRowId);
        }
    }

    private static void AssertMigratedState(SqliteConnection connection, FixtureSentinels sentinels)
    {
        Assert.Equal(
            ["doctor|1", "monitor|5"],
            DoctorTestDatabase.Rows(connection, "SELECT component,version FROM schema_version ORDER BY component;"));
        Assert.Equal(sentinels.RawRecordId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM raw_records WHERE id=$id;", ("$id", sentinels.RawRecordId)));
        Assert.Equal(sentinels.IngestionId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_ingestions WHERE id=$id;", ("$id", sentinels.IngestionId)));
        Assert.Equal(sentinels.TraceId, DoctorTestDatabase.Scalar(connection, "SELECT trace_id FROM monitor_traces WHERE trace_id=$id;", ("$id", sentinels.TraceId)));
        Assert.Equal(sentinels.TraceRowId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_traces WHERE trace_id=$id;", ("$id", sentinels.TraceId)));
        if (sentinels.SpanId is not null)
        {
            Assert.Equal(sentinels.SpanId, DoctorTestDatabase.Scalar(connection, "SELECT span_id FROM monitor_spans WHERE span_id=$id;", ("$id", sentinels.SpanId)));
            Assert.Equal(sentinels.SpanRowId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_spans WHERE span_id=$id;", ("$id", sentinels.SpanId)));
        }
        else
        {
            Assert.Null(sentinels.SpanRowId);
        }
        Assert.Equal("ok", DoctorTestDatabase.Scalar(connection, "PRAGMA integrity_check;"));
        Assert.Equal(2L, DoctorTestDatabase.Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('doctor_verifications','doctor_verification_evidence');"));
        Assert.Equal(5L, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal(1L, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='doctor';"));
        Assert.True(DoctorSchemaV1.IsValid(connection, transaction: null));
    }

    private static void AssertCurrentMonitorV5Schema(SqliteConnection connection)
    {
        Assert.Equal(
            [
                "id", "observation_id", "raw_record_id", "ingest_batch_id", "source_surface",
                "source_application_version", "source_adapter", "adapter_version", "schema_fingerprint",
                "inventory_hash", "compatibility_state", "reason_code", "next_action", "capture_content_state",
                "unknown_span_count", "unknown_event_count", "unknown_attribute_count", "overflow_distinct_count",
                "overflow_occurrence_count", "observed_at",
            ],
            Columns(connection, "source_schema_observations"));
        Assert.Equal(
            [
                "id", "source_observation_id", "kind", "name", "occurrence_count", "source_version_label",
                "first_observed_at", "last_observed_at", "opaque_sample_reference",
            ],
            Columns(connection, "source_unknown_observations"));

        AssertDefinitionContains(
            connection,
            "source_schema_observations",
            "id INTEGER PRIMARY KEY AUTOINCREMENT",
            "observation_id TEXT NOT NULL UNIQUE",
            "raw_record_id INTEGER NULL UNIQUE",
            "ingest_batch_id TEXT NULL UNIQUE",
            "source_surface TEXT NULL",
            "source_application_version TEXT NULL",
            "source_adapter TEXT NULL",
            "adapter_version TEXT NULL",
            "schema_fingerprint TEXT NULL",
            "inventory_hash TEXT NULL",
            "compatibility_state TEXT NOT NULL CHECK (compatibility_state IN ('supported', 'supported_with_unknown_fields', 'schema_drift_detected', 'unsupported_source_version', 'recognized_record_drop_detected', 'adapter_failure'))",
            "reason_code TEXT NULL CHECK (reason_code IS NULL OR reason_code IN ('unknown_fields_observed', 'unsupported_source_version', 'schema_drift_detected', 'recognized_record_drop_detected', 'adapter_parse_failure', 'adapter_exception'))",
            "next_action TEXT NOT NULL CHECK (next_action IN ('none', 'review_unknown_fields', 'use_compatible_source_or_update_adapter', 'capture_fixture_and_review_mapping', 'restore_mapping_or_update_versioned_golden', 'validate_payload_and_protocol', 'inspect_sanitized_adapter_failure'))",
            "capture_content_state TEXT NULL CHECK (capture_content_state IS NULL OR capture_content_state IN ('available', 'not_captured', 'redacted', 'unsupported'))",
            "unknown_span_count INTEGER NOT NULL CHECK (unknown_span_count >= 0)",
            "unknown_event_count INTEGER NOT NULL CHECK (unknown_event_count >= 0)",
            "unknown_attribute_count INTEGER NOT NULL CHECK (unknown_attribute_count >= 0)",
            "overflow_distinct_count INTEGER NOT NULL CHECK (overflow_distinct_count >= 0)",
            "overflow_occurrence_count INTEGER NOT NULL CHECK (overflow_occurrence_count >= 0)",
            "observed_at TEXT NOT NULL",
            "CHECK (compatibility_state = 'adapter_failure' OR capture_content_state IS NOT NULL)",
            "CHECK (compatibility_state = 'supported' OR reason_code IS NOT NULL)",
            "(compatibility_state = 'supported' AND reason_code IS NULL AND next_action = 'none')",
            "(compatibility_state = 'supported_with_unknown_fields' AND reason_code = 'unknown_fields_observed' AND next_action = 'review_unknown_fields')",
            "(compatibility_state = 'unsupported_source_version' AND reason_code = 'unsupported_source_version' AND next_action = 'use_compatible_source_or_update_adapter')",
            "(compatibility_state = 'schema_drift_detected' AND reason_code = 'schema_drift_detected' AND next_action = 'capture_fixture_and_review_mapping')",
            "(compatibility_state = 'recognized_record_drop_detected' AND reason_code = 'recognized_record_drop_detected' AND next_action = 'restore_mapping_or_update_versioned_golden')",
            "(compatibility_state = 'adapter_failure' AND reason_code = 'adapter_parse_failure' AND next_action = 'validate_payload_and_protocol')",
            "(compatibility_state = 'adapter_failure' AND reason_code = 'adapter_exception' AND next_action = 'inspect_sanitized_adapter_failure')");
        AssertDefinitionContains(
            connection,
            "source_unknown_observations",
            "id INTEGER PRIMARY KEY AUTOINCREMENT",
            "source_observation_id INTEGER NOT NULL",
            "kind TEXT NOT NULL CHECK (kind IN ('span', 'event', 'attribute'))",
            "name TEXT NOT NULL",
            "occurrence_count INTEGER NOT NULL CHECK (occurrence_count BETWEEN 1 AND 1000000)",
            "source_version_label TEXT NULL",
            "first_observed_at TEXT NOT NULL",
            "last_observed_at TEXT NOT NULL",
            "opaque_sample_reference TEXT NOT NULL",
            "UNIQUE(source_observation_id, kind, name)",
            "CHECK (first_observed_at <= last_observed_at)");

        Assert.Equal(
            [
                "IX_source_schema_observations_cursor|CREATE INDEX IX_source_schema_observations_cursor ON source_schema_observations(id)",
                "IX_source_unknown_observations_cursor|CREATE INDEX IX_source_unknown_observations_cursor ON source_unknown_observations(source_observation_id, id)",
            ],
            DoctorTestDatabase.Rows(
                connection,
                "SELECT name,sql FROM sqlite_schema WHERE type='index' AND name LIKE 'IX_source_%_cursor' ORDER BY name;"));
    }

    private static IReadOnlyList<string> Columns(SqliteConnection connection, string table) =>
        DoctorTestDatabase.Rows(connection, $"SELECT name FROM pragma_table_xinfo('{table}') ORDER BY cid;");

    private static void AssertDefinitionContains(SqliteConnection connection, string table, params string[] expectedFragments)
    {
        var definition = Assert.IsType<string>(DoctorTestDatabase.Scalar(
            connection,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table;",
            ("$table", table)));
        var normalizedDefinition = NormalizeSql(definition);
        Assert.All(expectedFragments, fragment =>
            Assert.Contains(NormalizeSql(fragment), normalizedDefinition, StringComparison.Ordinal));
    }

    private static string NormalizeSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static string SnapshotDoctorRows(SqliteConnection connection) => string.Join(
        '\n',
        DoctorTestDatabase.Rows(connection, "SELECT * FROM doctor_verifications ORDER BY verification_id;")
            .Concat(DoctorTestDatabase.Rows(connection, "SELECT * FROM doctor_verification_evidence ORDER BY candidate_id;")));

    private static string SnapshotDatabase(SqliteConnection connection)
    {
        var schema = DoctorTestDatabase.Rows(
            connection,
            "SELECT type,name,tbl_name,coalesce(sql,'<null>') FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;");
        var tables = DoctorTestDatabase.Rows(
            connection,
            "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        var rows = tables.SelectMany(table => new[] { $"TABLE:{table}" }.Concat(
            DoctorTestDatabase.Rows(connection, $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" ORDER BY rowid;")));
        return string.Join('\n', schema.Concat(rows));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record FixtureManifest(
        string Component,
        string GenerationCommand,
        string GitStatusCommand,
        IReadOnlyList<FixtureEntry> Fixtures);

    private sealed record FixtureEntry(
        int Version,
        string File,
        string SourceCommit,
        string Sha256,
        string GitStatusBefore,
        string GitStatusAfter,
        FixtureSentinels Sentinels);

    private sealed record FixtureSentinels(
        long RawRecordId,
        long IngestionId,
        long TraceRowId,
        string TraceId,
        long? SpanRowId,
        string? SpanId);
}

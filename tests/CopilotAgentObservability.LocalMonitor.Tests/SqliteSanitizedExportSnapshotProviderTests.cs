using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Globalization;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Pricing;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.InstructionFindings;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteSanitizedExportSnapshotProviderTests
{
    private const string SessionA = "01900000-0000-7000-8000-0000000000a1";
    private const string SessionB = "01900000-0000-7000-8000-0000000000b1";
    private const string UnrelatedSession = "01900000-0000-7000-8000-0000000000c1";

    [Fact]
    public void Capture_ProjectsOnlySafeMonitorAndExactSessionMetadata()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));

        Assert.True(result.Success, result.ErrorCode);
        var record = Assert.Single(result.Snapshot!.Records);
        Assert.Equal("repository_metadata_projection", record.RecordType);
        Assert.Equal(SessionA, record.SessionId);
        Assert.Equal("trace-a", record.TraceId);
        Assert.Equal("safe-repository", record.RepositoryName);
        Assert.Equal("safe-workspace", record.WorkspaceLabel);
        Assert.DoesNotContain("raw-secret-marker", Encoding.UTF8.GetString(record.CanonicalBytes), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(record.CanonicalBytes);
        Assert.Equal("repository-metadata-projection.v1", json.RootElement.GetProperty("schema_version").GetString());
    }

    [Fact]
    public void Capture_RejectsInvalidSelectionBeforeOpeningStore()
    {
        var result = new SqliteSanitizedExportSnapshotProvider("missing.db")
            .Capture(new(SessionIds: [""]));

        Assert.False(result.Success);
        Assert.Equal("invalid_selection", result.ErrorCode);
    }

    [Fact]
    public void Capture_DeduplicatesByteIdenticalExactSessionBindings()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute($"INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status) VALUES('run-b','{SessionA}','copilot-cli','trace-a','completed');");

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath).Capture(new(TraceIds: ["trace-a"]));

        Assert.True(result.Success, result.ErrorCode);
        Assert.Single(result.Snapshot!.Records);
    }

    [Fact]
    public void Capture_PreservesSameSessionTraceProvenanceAcrossSourceSurfaces()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute($"INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status) VALUES('run-b','{SessionA}','claude-code','trace-a','completed');");
        var provider = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath);

        var first = provider.Capture(new(TraceIds: ["trace-a"]));
        var second = provider.Capture(new(TraceIds: ["trace-a"]));
        var selectedSurface = provider.Capture(new(TraceIds: ["trace-a"], SourceSurfaces: ["claude-code"]));

        Assert.True(first.Success, first.ErrorCode);
        Assert.Equal(["claude-code", "copilot-cli"], first.Snapshot!.Records.Select(record => record.SourceSurface).Order(StringComparer.Ordinal));
        Assert.Equal(2, first.Snapshot.Records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first.Snapshot.SnapshotId, second.Snapshot!.SnapshotId);
        Assert.Equal("claude-code", Assert.Single(selectedSurface.Snapshot!.Records).SourceSurface);
    }

    [Fact]
    public void Capture_FailsClosedWhenTraceBindsDistinctSessions()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute($"""
            INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES('{SessionB}','active','unbound','2026-07-22T00:00:00.0000000Z','not_captured','2026-07-22T00:00:00.0000000Z','2026-07-22T00:00:00.0000000Z');
            INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status)
            VALUES('run-b','{SessionB}','claude-code','trace-a','completed');
            """);

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath).Capture(new(TraceIds: ["trace-a"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public void Capture_UsesSharedInstructionConsumerAndPreservesExactBytes()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        var bytes = InstructionBytes();
        fixture.SeedFinding(bytes);

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath).Capture(new(ReceiptTypes: ["instruction_finding_handoff"]));

        Assert.True(result.Success, result.ErrorCode);
        var record = Assert.Single(result.Snapshot!.Records);
        Assert.Equal(InstructionFindingHandoffConsumerV1.Validate(bytes).ToString(), record.RecordId);
        Assert.Equal(bytes, record.CanonicalBytes);
    }

    [Fact]
    public void Capture_FailsClosedOnPartialOptionalProducerSchema()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute("CREATE TABLE instruction_finding_handoffs(analysis_run_id INTEGER PRIMARY KEY,payload_json TEXT NOT NULL);");

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath).Capture(new());

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public void Capture_UnrelatedRowsDoNotChangeSelectedSnapshotIdentityOrVersions()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        var provider = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath);
        var first = provider.Capture(new(TraceIds: ["trace-a"]));
        fixture.SeedUnrelatedSessionEvent();

        var second = provider.Capture(new(TraceIds: ["trace-a"]));

        Assert.True(first.Success, first.ErrorCode);
        Assert.True(second.Success, second.ErrorCode);
        Assert.Equal(first.Snapshot!.SnapshotId, second.Snapshot!.SnapshotId);
        Assert.Equal(first.Snapshot.AgentVersions, second.Snapshot.AgentVersions);
    }

    [Fact]
    public void Capture_EnvelopeMetadataChangeChangesSnapshotIdentityWhenCarrierBytesDoNot()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.SeedFinding(InstructionBytes());
        var provider = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath);

        var first = provider.Capture(new(ReceiptTypes: ["instruction_finding_handoff"]));
        fixture.Execute("UPDATE instruction_finding_handoffs SET created_at='2026-07-22T00:00:01.0000000Z';");
        var second = provider.Capture(new(ReceiptTypes: ["instruction_finding_handoff"]));

        Assert.True(first.Success, first.ErrorCode);
        Assert.True(second.Success, second.ErrorCode);
        Assert.Equal(Assert.Single(first.Snapshot!.Records).CanonicalBytes, Assert.Single(second.Snapshot!.Records).CanonicalBytes);
        Assert.NotEqual(first.Snapshot.SnapshotId, second.Snapshot.SnapshotId);
    }

    [Fact]
    public void Capture_AppliesAlertSelectorsOnlyAfterBoundedExactCarrierValidation()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.SeedAlert(SanitizedExportAlertFixture.Bytes());
        var provider = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath);

        var excluded = provider.Capture(new(SourceSurfaces: ["claude-code"], ReceiptTypes: ["alert_receipt"]));
        var included = provider.Capture(new(SourceSurfaces: ["github-copilot"], ReceiptTypes: ["alert_receipt"]));

        Assert.True(excluded.Success, excluded.ErrorCode);
        Assert.Empty(excluded.Snapshot!.Records);
        Assert.Equal("missing", excluded.Snapshot.Capabilities.AlertReceipts);
        Assert.True(included.Success, included.ErrorCode);
        Assert.Equal("alert_receipt", Assert.Single(included.Snapshot!.Records).RecordType);
        Assert.Equal("available", included.Snapshot.Capabilities.AlertReceipts);
    }

    [Fact]
    public void Capture_V2OnlyAlertEngineDoesNotReadOrExportV2Payloads()
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        fixture.SeedOpaqueV2Alert("pricing.estimate.v1 C:\\private\\must-not-read");

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(ReceiptTypes: ["alert_receipt"]));

        Assert.True(result.Success, result.ErrorCode);
        Assert.Empty(result.Snapshot!.Records);
        Assert.Equal("missing", result.Snapshot.Capabilities.AlertReceipts);
        Assert.Equal("2", result.Snapshot.ProcessingVersions!["alert_engine_schema"]);
    }

    [Fact]
    public void Capture_MixedV2AlertEngineExportsOnlyStrictV1Receipt()
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        var receiptV1 = SanitizedExportAlertFixture.Bytes();
        fixture.SeedAlert(receiptV1);
        fixture.SeedOpaqueV2Alert("private-override-must-not-read");

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(ReceiptTypes: ["alert_receipt"]));

        Assert.True(result.Success, result.ErrorCode);
        var record = Assert.Single(result.Snapshot!.Records);
        Assert.Equal(receiptV1, record.CanonicalBytes);
        Assert.Equal("alert_receipt", record.RecordType);
        Assert.Equal("2", result.Snapshot.ProcessingVersions!["alert_engine_schema"]);
        Assert.DoesNotContain("private-override-must-not-read", Encoding.UTF8.GetString(record.CanonicalBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_CorruptSelectedV1ReceiptBesideOwnerValidV2FailsClosed()
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        fixture.SeedAlert(SanitizedExportAlertFixture.Bytes());
        fixture.SeedValidV2Alert();
        fixture.Execute("""
            UPDATE alert_receipts
            SET canonical_json=replace(canonical_json,'sanitized-alert-receipt.v1','sanitized-alert-receipt.v2')
            WHERE schema_version='alert.receipt.v1';
            """);

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(ReceiptTypes: ["alert_receipt"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public void Capture_SelectedV1ReceiptWithMissingParentBesideOwnerValidV2FailsClosed()
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        fixture.SeedAlert(SanitizedExportAlertFixture.Bytes());
        fixture.SeedValidV2Alert();
        fixture.Execute("""
            PRAGMA foreign_keys=OFF;
            DELETE FROM alert_evaluations WHERE schema_version='alert.evaluation.v1';
            """);

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(ReceiptTypes: ["alert_receipt"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public void Capture_MixedV2AndPricingStoreExportsExactV1WithoutPricingReads()
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        var receiptV1 = SanitizedExportAlertFixture.Bytes();
        fixture.SeedAlert(receiptV1);
        fixture.SeedOpaqueV2Alert("semantically-invalid-v2");
        fixture.PoisonV2PayloadsBeyondV1CarrierLimit();
        fixture.SeedExactPricingComponent();
        var statements = new List<string>();
        var reads = new List<(string Table, string Column)>();

        var result = new SqliteSanitizedExportSnapshotProvider(
                fixture.DatabasePath,
                statements.Add,
                (table, column) => reads.Add((table, column)))
            .Capture(new(ReceiptTypes: ["alert_receipt"]));

        Assert.True(result.Success, result.ErrorCode);
        var record = Assert.Single(result.Snapshot!.Records);
        Assert.Equal(receiptV1, record.CanonicalBytes);
        Assert.Equal("alert_receipt", record.RecordType);
        Assert.Equal("available", result.Snapshot.Capabilities.AlertReceipts);
        Assert.Equal("2", result.Snapshot.ProcessingVersions!["alert_engine_schema"]);
        Assert.Contains(reads, read => read == ("alert_receipts", "canonical_json"));
        Assert.DoesNotContain(reads, read =>
            read is ("alert_evaluations", "canonical_json") or ("alert_suppressions", "canonical_json")
            || read.Table.StartsWith("pricing_", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, statement => statement.Contains("pricing_", StringComparison.Ordinal));
        var payloadStatements = statements
            .Where(statement => statement.Contains("canonical_json", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(
            payloadStatements,
            statement => statement.Contains(
                "FROM alert_receipts WHERE schema_version='alert.receipt.v1'",
                StringComparison.Ordinal));
        Assert.Contains(
            payloadStatements,
            statement => statement.Contains(
                "SELECT schema_version,evaluation_id,canonical_json FROM alert_receipts WHERE alert_id=",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("schema_version='alert.evaluation.v2'")]
    [InlineData("input_hash='dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'")]
    [InlineData("configuration_version='tampered-v1'")]
    [InlineData("configuration_hash='dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'")]
    public void Capture_V2AlertEngineRejectsSelectedV1ReceiptWithTamperedParentScalar(string assignment)
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        fixture.SeedAlert(SanitizedExportAlertFixture.Bytes());
        fixture.Execute($"UPDATE alert_evaluations SET {assignment};");

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(ReceiptTypes: ["alert_receipt"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public void Capture_RejectsCounterfeitV2AlertEngineStructure()
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        fixture.Execute("CREATE TRIGGER counterfeit_alert_owner AFTER INSERT ON alert_receipts BEGIN SELECT 1; END;");

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath).Capture(new());

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public void Capture_RejectsFutureAlertEngineVersion()
    {
        using var fixture = new Fixture();
        fixture.InitializeAlertEngineV2();
        fixture.Execute("UPDATE schema_version SET version=3 WHERE component='alert_engine';");

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath).Capture(new());

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public void Capture_FailsClosedWhenOpaqueAlertCandidateScanExceedsBoundEvenIfSelectorWouldExcludeRows()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.SeedOpaqueAlertCandidates(SanitizedExportLimits.MaximumRecords + 1);

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(SourceSurfaces: ["claude-code"], ReceiptTypes: ["alert_receipt"]));

        Assert.False(result.Success);
        Assert.Equal("selection_limit_exceeded", result.ErrorCode);
    }

    [Fact]
    public void Capture_RejectsOversizedAlertBeforeSemanticJsonValidation()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        var alertId = new string('a', 64);
        var evaluationId = new string('e', 64);
        fixture.SeedAlert(Encoding.UTF8.GetBytes($"{{\"alert_id\":\"{alertId}\",\"evaluation_id\":\"{evaluationId}\",\"padding\":\"{new string('x', SanitizedExportLimits.MaximumRecordBytes)}\"}}"));

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath).Capture(new(ReceiptTypes: ["alert_receipt"]));

        Assert.False(result.Success);
        Assert.Equal("uncompressed_size_limit_exceeded", result.ErrorCode);
    }

    [Fact]
    public void Capture_RejectsMalformedTerminalFactPairWithFixedStoreFailure()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute("""
            PRAGMA ignore_check_constraints=ON;
            UPDATE session_events
            SET terminal_policy_version=NULL
            WHERE terminal_outcome IS NOT NULL;
            PRAGMA ignore_check_constraints=OFF;
            """);

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("failed-sdk-task-complete")]
    [InlineData("failed-claude-hook")]
    [InlineData("fact-on-claude-otel")]
    public void Capture_RejectsEachIllegalTerminalTupleOutcomeWithFixedStoreFailure(string corruption)
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute(
            corruption switch
            {
                "failed-sdk-task-complete" => """
                    UPDATE session_events SET terminal_outcome='failed'
                    WHERE source_event_id='complete-a';
                    UPDATE sessions SET status='failed';
                    """,
                "failed-claude-hook" => """
                    UPDATE session_events
                    SET source_adapter='claude-code-hook',source_surface='claude-code',
                        type='SessionEnd',terminal_outcome='failed'
                    WHERE source_event_id='complete-a';
                    UPDATE sessions SET status='failed';
                    """,
                "fact-on-claude-otel" => """
                    UPDATE session_events
                    SET source_adapter='claude-code-otel',source_surface='claude-code',
                        source_event_id='11111111111111111111111111111111/2222222222222222',
                        type='otel.span',terminal_outcome='clean'
                    WHERE source_event_id='complete-a';
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            });

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("ended_at")]
    [InlineData("completeness")]
    public void Capture_RejectsAggregateAndCompletenessDriftWithFixedStoreFailure(string corruption)
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute(
            corruption switch
            {
                "status" => "UPDATE sessions SET status='active';",
                "ended_at" => "UPDATE sessions SET ended_at='2026-07-22T00:00:00.0000001+00:00';",
                "completeness" => "UPDATE sessions SET completeness='partial';",
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            });

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("monitor")]
    public void Capture_BlobRequiredSchemaVersionReturnsFixedStoreFailure(string component)
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute($"UPDATE schema_version SET version=CAST(x'00' AS BLOB) WHERE component='{component}';");
        Assert.Equal("blob", fixture.Scalar<string>($"SELECT typeof(version) FROM schema_version WHERE component='{component}';"));

        var result = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));

        Assert.False(result.Success);
        Assert.Equal("snapshot_store_unavailable", result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Capture_SharedValidationReadsFactsButNeverContentOrRawStores()
    {
        using var fixture = new Fixture();
        fixture.SeedTraceAndSession();
        fixture.Execute("""
            INSERT INTO session_event_content(
                event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
            VALUES(
                '01900000-0000-7000-9000-0000000000a2','application/json',
                '{"private":"content-read-marker"}',
                '2026-07-22T00:00:00.0000000+00:00',
                '2026-10-20T00:00:00.0000000+00:00',zeroblob(32));
            """);
        var statements = new List<string>();
        var reads = new List<(string Table, string Column)>();

        var result = new SqliteSanitizedExportSnapshotProvider(
                fixture.DatabasePath,
                statements.Add,
                (table, column) => reads.Add((table, column)))
            .Capture(new(TraceIds: ["trace-a"]));

        Assert.True(result.Success, result.ErrorCode);
        Assert.NotEmpty(statements);
        Assert.Contains(reads, read => read == ("session_events", "terminal_outcome"));
        Assert.Contains(reads, read => read == ("session_events", "terminal_policy_version"));
        Assert.DoesNotContain(reads, read => read.Table == "session_event_content");
        Assert.DoesNotContain(reads, read => read.Table == "raw_records");
        Assert.DoesNotContain(
            result.Snapshot!.Records,
            record => Encoding.UTF8.GetString(record.CanonicalBytes).Contains("content-read-marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_PrivateTerminalFactsNeverEnterCarriersHashesOrFixedDiagnostics()
    {
        using var clean = new Fixture();
        using var failed = new Fixture();
        using var malformed = new Fixture();
        clean.SeedTraceAndSession();
        failed.SeedTraceAndSession();
        malformed.SeedTraceAndSession();
        MakeCompatibleTerminalFact(clean, "clean", "completed");
        MakeCompatibleTerminalFact(failed, "failed", "failed");
        malformed.Execute("""
            PRAGMA ignore_check_constraints=ON;
            UPDATE session_events
            SET terminal_outcome='future_private_outcome',terminal_policy_version=1
            WHERE source_event_id='complete-a';
            PRAGMA ignore_check_constraints=OFF;
            """);
        var cleanCapture = new SqliteSanitizedExportSnapshotProvider(clean.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));
        var failedCapture = new SqliteSanitizedExportSnapshotProvider(failed.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));
        var malformedCapture = new SqliteSanitizedExportSnapshotProvider(malformed.DatabasePath)
            .Capture(new(TraceIds: ["trace-a"]));

        Assert.True(cleanCapture.Success, cleanCapture.ErrorCode);
        Assert.True(failedCapture.Success, failedCapture.ErrorCode);
        Assert.Equal(cleanCapture.Snapshot!.SnapshotId, failedCapture.Snapshot!.SnapshotId);
        Assert.Equal(
            cleanCapture.Snapshot.Records.Select(record => record.CanonicalBytes),
            failedCapture.Snapshot.Records.Select(record => record.CanonicalBytes));
        Assert.False(malformedCapture.Success);
        Assert.Equal("snapshot_store_unavailable", malformedCapture.ErrorCode);
        var publicOutput = string.Join(
            "\n",
            JsonSerializer.Serialize(cleanCapture),
            JsonSerializer.Serialize(failedCapture),
            JsonSerializer.Serialize(malformedCapture));
        Assert.DoesNotContain("terminal_outcome", publicOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("terminal_policy_version", publicOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("future_private_outcome", publicOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\"clean\"", publicOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\"failed\"", publicOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void FramedInventoryHashDistinguishesDelimiterBoundaryCollisions()
    {
        Assert.NotEqual(
            SqliteSanitizedExportSnapshotProvider.FramedHash("a", "b\0c"),
            SqliteSanitizedExportSnapshotProvider.FramedHash("a\0b", "c"));
    }

    private static void MakeCompatibleTerminalFact(Fixture fixture, string outcome, string status) =>
        fixture.Execute($$"""
            UPDATE session_events
            SET source_adapter='copilot-compatible-hook',source_surface='copilot-cli',
                type='SessionEnd',terminal_outcome='{{outcome}}'
            WHERE source_event_id='complete-a';
            UPDATE sessions SET status='{{status}}';
            """);

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"sanitized-export-provider-{Guid.NewGuid():N}");

        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "monitor.db");
            new RawTelemetryStore(DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
            new SqliteSessionStore(DatabasePath).CreateSchema();
        }

        internal string DatabasePath { get; }

        internal void SeedTraceAndSession()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $$"""
                INSERT INTO monitor_traces(trace_id,client_kind,last_seen_at,projected_at,repository_name,workspace_label,repo_snapshot)
                VALUES('trace-a','github-copilot-cli','2026-07-22T00:00:00.0000000Z','2026-07-22T00:00:00.0000000Z','safe-repository','safe-workspace','safe-snapshot');
                INSERT INTO sessions(session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES('{{SessionA}}','completed','full','must-not-substitute','must-not-substitute','2026-07-22T00:00:00.0000000+00:00','2026-07-22T00:00:00.0000000+00:00','2026-07-22T00:00:00.0000000+00:00','not_captured','2026-07-22T00:00:00.0000000+00:00','2026-07-22T00:00:00.0000000+00:00');
                INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
                VALUES('{{SessionA}}','copilot-sdk','native-session-a','native','2026-07-22T00:00:00.0000000+00:00');
                INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,started_at,ended_at,status)
                VALUES('run-a','{{SessionA}}','copilot-cli','trace-a','2026-07-22T00:00:00.0000000+00:00','2026-07-22T00:00:00.0000000+00:00','completed');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
                VALUES('01900000-0000-7000-9000-0000000000a1','{{SessionA}}','run-a','copilot-sdk','copilot-sdk-stream','start-a','session.start','2026-07-22T00:00:00.0000000+00:00','not_captured');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
                VALUES('01900000-0000-7000-9000-0000000000a2','{{SessionA}}','run-a','copilot-sdk','copilot-sdk-stream','message-a','user.message','2026-07-22T00:00:00.0000000+00:00','not_captured');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
                VALUES('01900000-0000-7000-9000-0000000000a3','{{SessionA}}','run-a','copilot-cli','trace-a','otel-exact','trace-a/span-a','otel.span','2026-07-22T00:00:00.0000000+00:00','not_captured');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,terminal_outcome,terminal_policy_version)
                VALUES('01900000-0000-7000-9000-0000000000a4','{{SessionA}}','run-a','copilot-sdk','copilot-sdk-stream','complete-a','session.task_complete','2026-07-22T00:00:00.0000000+00:00','not_captured','clean',1);
                INSERT INTO raw_records(source,trace_id,received_at,payload_json,schema_version,retention_owner_token)
                VALUES('raw-otlp','trace-a','2026-07-22T00:00:00.0000000Z','{"raw":"raw-secret-marker"}',1,zeroblob(32));
                """;
            command.ExecuteNonQuery();
        }

        internal void SeedUnrelatedSessionEvent() => Execute($"""
            INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES('{UnrelatedSession}','active','unbound','2026-07-22T00:00:00.0000000Z','not_captured','2026-07-22T00:00:00.0000000Z','2026-07-22T00:00:00.0000000Z');
            INSERT INTO session_events(event_id,session_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version)
            VALUES('01900000-0000-7000-9000-0000000000c1','{UnrelatedSession}','claude-code','fixture','source-unrelated','capture.started','2026-07-22T00:00:00.0000000Z','not_captured','unrelated-version');
            """);

        internal void SeedAlert(byte[] canonicalBytes)
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
            Assert.Equal(AlertStoreStatus.Success, new SqliteAlertEngineStore(connectionString).Initialize().Status);
            using var document = JsonDocument.Parse(canonicalBytes);
            var alertId = document.RootElement.GetProperty("alert_id").GetString()!;
            var evaluationId = document.RootElement.GetProperty("evaluation_id").GetString()!;
            var inputHash = document.RootElement.TryGetProperty("evaluation_input_hash", out var inputProperty)
                ? inputProperty.GetString()!
                : new string('b', 64);
            var configurationVersion = document.RootElement.TryGetProperty("configuration_version", out var configurationVersionProperty)
                ? configurationVersionProperty.GetString()!
                : "fixture-v1";
            var configurationHash = document.RootElement.TryGetProperty("configuration_hash", out var configurationHashProperty)
                ? configurationHashProperty.GetString()!
                : new string('c', 64);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO alert_evaluations(evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json)
                VALUES($evaluation,'alert.evaluation.v1',$input,$configuration_version,$configuration_hash,$evaluation_json);
                INSERT INTO alert_receipts(alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json)
                VALUES($alert,$evaluation,0,'alert.receipt.v1',$receipt);
                """;
            command.Parameters.AddWithValue("$alert", alertId);
            command.Parameters.AddWithValue("$evaluation", evaluationId);
            command.Parameters.AddWithValue("$input", inputHash);
            command.Parameters.AddWithValue("$configuration_version", configurationVersion);
            command.Parameters.AddWithValue("$configuration_hash", configurationHash);
            command.Parameters.AddWithValue("$evaluation_json", $"{{\"evaluation_id\":\"{evaluationId}\"}}");
            command.Parameters.AddWithValue("$receipt", Encoding.UTF8.GetString(canonicalBytes));
            command.ExecuteNonQuery();
        }

        internal void InitializeAlertEngineV2()
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
            Assert.Equal(AlertEngineStoreStatusV2.Success, new SqliteAlertEngineStore(connectionString).InitializeV2().Status);
        }

        internal void SeedExactPricingComponent()
        {
            using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction(deferred: false);
                RuntimeBackupSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }

            var store = new SqlitePricingStore(DatabasePath);
            store.CreateSchema();
            var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
            Assert.Equal(
                PricingStoreStatus.Success,
                store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);

            using var read = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            read.Open();
            Assert.True(PricingSchemaV1.IsValid(read, null));
            Assert.True(PricingSchemaV1.ValidateRows(read, null));
            Assert.Equal(13, PricingSchemaV1.OwnedObjects.Count(item => item.Type == "table"));
            var expected = PricingSchemaV1.OwnedObjects
                .Select(item => (item.Type, item.Name, item.TableName))
                .ToArray();
            using var command = read.CreateCommand();
            command.CommandText = "SELECT type,name,tbl_name FROM sqlite_schema WHERE name GLOB 'pricing_*' ORDER BY type,name;";
            using var reader = command.ExecuteReader();
            var actual = new List<(string Type, string Name, string TableName)>();
            while (reader.Read()) actual.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            Assert.Equal(expected, actual);
        }

        internal void SeedValidV2Alert()
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
            Assert.Equal(
                AlertEngineStoreStatusV2.Success,
                new SqliteAlertEngineStore(connectionString).Append(SanitizedExportAlertFixture.EvaluationV2()).Status);
        }

        internal void SeedOpaqueV2Alert(string opaquePayload)
        {
            var evaluationId = new string('e', 64);
            var alertId = new string('a', 64);
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO alert_evaluations(evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json)
                VALUES($evaluation,'alert.evaluation.v2',$input,'fixture-v2',$configuration,$evaluation_json);
                INSERT INTO alert_receipts(alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json)
                VALUES($alert,$evaluation,0,'alert.receipt.v2',$receipt);
                INSERT INTO alert_suppressions(evaluation_id,suppression_ordinal,rule_id,rule_version,code,canonical_json)
                VALUES($evaluation,0,'fixture-rule','2','rule-disabled',$suppression);
                """;
            command.Parameters.AddWithValue("$alert", alertId);
            command.Parameters.AddWithValue("$evaluation", evaluationId);
            command.Parameters.AddWithValue("$input", new string('b', 64));
            command.Parameters.AddWithValue("$configuration", new string('c', 64));
            command.Parameters.AddWithValue("$evaluation_json", $"{{\"evaluation_id\":\"{evaluationId}\",\"opaque\":{JsonSerializer.Serialize(opaquePayload)}}}");
            command.Parameters.AddWithValue("$receipt", $"{{\"alert_id\":\"{alertId}\",\"evaluation_id\":\"{evaluationId}\",\"opaque\":{JsonSerializer.Serialize(opaquePayload)}}}");
            command.Parameters.AddWithValue("$suppression", $"{{\"evaluation_id\":\"{evaluationId}\",\"opaque\":{JsonSerializer.Serialize(opaquePayload)}}}");
            command.ExecuteNonQuery();
        }

        internal void PoisonV2PayloadsBeyondV1CarrierLimit()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE alert_evaluations
                SET canonical_json=json_object(
                    'evaluation_id',evaluation_id,
                    'opaque',printf('%.*c',$length,'x'))
                WHERE schema_version='alert.evaluation.v2';
                UPDATE alert_receipts
                SET canonical_json=json_object(
                    'alert_id',alert_id,
                    'evaluation_id',evaluation_id,
                    'opaque',printf('%.*c',$length,'x'))
                WHERE schema_version='alert.receipt.v2';
                UPDATE alert_suppressions
                SET canonical_json=json_object(
                    'evaluation_id',evaluation_id,
                    'opaque',printf('%.*c',$length,'x'));
                """;
            command.Parameters.AddWithValue("$length", SanitizedExportLimits.MaximumRecordBytes + 1);
            command.ExecuteNonQuery();
        }

        internal void SeedOpaqueAlertCandidates(int count)
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
            Assert.Equal(AlertStoreStatus.Success, new SqliteAlertEngineStore(connectionString).Initialize().Status);
            var evaluationId = new string('e', 64);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using (var evaluation = connection.CreateCommand())
            {
                evaluation.Transaction = transaction;
                evaluation.CommandText = "INSERT INTO alert_evaluations(evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json) VALUES($id,'alert.evaluation.v1',$input,'fixture-v1',$configuration,$json);";
                evaluation.Parameters.AddWithValue("$id", evaluationId);
                evaluation.Parameters.AddWithValue("$input", new string('b', 64));
                evaluation.Parameters.AddWithValue("$configuration", new string('c', 64));
                evaluation.Parameters.AddWithValue("$json", $"{{\"evaluation_id\":\"{evaluationId}\"}}");
                evaluation.ExecuteNonQuery();
            }
            for (var index = 1; index <= count; index++)
            {
                var alertId = index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
                using var receipt = connection.CreateCommand();
                receipt.Transaction = transaction;
                receipt.CommandText = "INSERT INTO alert_receipts(alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json) VALUES($alert,$evaluation,$ordinal,'alert.receipt.v1',$json);";
                receipt.Parameters.AddWithValue("$alert", alertId);
                receipt.Parameters.AddWithValue("$evaluation", evaluationId);
                receipt.Parameters.AddWithValue("$ordinal", index - 1);
                receipt.Parameters.AddWithValue("$json", $"{{\"alert_id\":\"{alertId}\",\"evaluation_id\":\"{evaluationId}\"}}");
                receipt.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        internal void SeedFinding(byte[] canonicalBytes)
        {
            var analysisRunId = InstructionFindingHandoffConsumerV1.Validate(canonicalBytes);
            Execute("""
                CREATE TABLE instruction_finding_handoffs(
                    analysis_run_id INTEGER PRIMARY KEY,
                    schema_version TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64 AND payload_sha256 = lower(payload_sha256)),
                    created_at TEXT NOT NULL
                );
                """);
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO instruction_finding_handoffs(analysis_run_id,schema_version,payload_json,payload_sha256,created_at) VALUES($id,'instruction-finding-handoff.v1',$payload,$sha,'2026-07-22T00:00:00.0000000Z');";
            command.Parameters.AddWithValue("$id", analysisRunId);
            command.Parameters.AddWithValue("$payload", Encoding.UTF8.GetString(canonicalBytes));
            command.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(canonicalBytes)));
            command.ExecuteNonQuery();
        }

        internal void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal T Scalar<T>(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private static byte[] InstructionBytes()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "specifications", "contracts", "instruction-findings", "v1", "instruction-finding-handoff.canonical.base64");
            if (!File.Exists(path)) continue;
            return Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        throw new DirectoryNotFoundException();
    }
}

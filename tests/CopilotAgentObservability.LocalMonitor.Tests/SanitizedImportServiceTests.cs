using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.SanitizedImport;
using CopilotAgentObservability.SanitizedExport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedImportServiceTests
{
    [Fact]
    public void Preview_ValidFrozenBundleReportsExactMigrationAndZeroRawRetentionItems()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();

        var preview = store.Preview(GoldenBundle());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(SanitizedImportContractVersions.Preview, preview.SchemaVersion);
        Assert.Equal("sanitized-evidence-manifest.v1", preview.ManifestSchemaVersion);
        Assert.Equal("sanitized-evidence-bundle.v1", preview.BundleSchemaVersion);
        Assert.Equal("sanitized-evidence", preview.BundleProfile);
        Assert.Equal("compatible", preview.Compatibility);
        Assert.Equal(1, preview.Migration.Version);
        Assert.Equal("sanitized-evidence-bundle.v1->sanitized-import-store.v1", preview.Migration.Chain);
        Assert.Equal(64, preview.Migration.ChainSha256.Length);
        Assert.Equal(1, preview.NewRecords);
        Assert.Equal(0, preview.DuplicateRecords);
        Assert.Equal(0, preview.ConflictRecords);
        Assert.True(preview.CanCommit);
        Assert.Equal(1, preview.ExpectedChanges.Records);
        Assert.Equal(1, preview.ExpectedChanges.Origins);
        Assert.True(preview.ExpectedChanges.GraphNodes >= 3);
        Assert.True(preview.ExpectedChanges.GraphEdges >= 2);
        Assert.Equal(1, preview.ExpectedChanges.HistoryRows);
        Assert.Equal(0, preview.ExpectedChanges.RawRetentionItems);
    }

    [Fact]
    public void Commit_ReplayIsIdempotentAndCreatesNoRetentionItem()
    {
        using var temp = new MonitorTempDirectory();
        var catalog = new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider);
        catalog.CreateSchema();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var beforeRetention = Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM retention_items;");
        var preview = store.Preview(GoldenBundle());

        var first = store.Commit(GoldenBundle(), preview.PreviewDigest!);
        var replay = store.Commit(GoldenBundle(), preview.PreviewDigest!);

        Assert.True(first.Success, first.ErrorCode);
        Assert.False(first.IdempotentReplay);
        Assert.Equal(1, first.NewRecords);
        Assert.True(replay.Success, replay.ErrorCode);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.ImportId, replay.ImportId);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
        Assert.Equal(beforeRetention, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM retention_items;"));
        Assert.Single(store.ListHistory(100).Items);

        var localRecordId = Text(temp.DatabasePath, "SELECT local_record_id FROM sanitized_import_records;");
        var resolution = catalog.ResolveMutationTarget(new(RetentionMutationTargetKind.Item, localRecordId));
        Assert.Equal(RetentionMutationTargetResolutionOutcome.NotApplicable, resolution.Outcome);
        Assert.Equal(RetentionMutationErrorCodes.TargetNotApplicable, resolution.ErrorCode);
    }

    [Fact]
    public void Commit_SameIdentityDifferentCanonicalBytesRejectsWholeImportWithoutHistory()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var firstBundle = RepositoryBundle("same-record", "repository-a");
        var secondBundle = RepositoryBundle("same-record", "repository-b");
        var firstPreview = store.Preview(firstBundle);
        Assert.True(store.Commit(firstBundle, firstPreview.PreviewDigest!).Success);

        var conflictPreview = store.Preview(secondBundle);
        var result = store.Commit(secondBundle, conflictPreview.PreviewDigest!);

        Assert.True(conflictPreview.Success, conflictPreview.ErrorCode);
        Assert.False(conflictPreview.CanCommit);
        Assert.Equal(1, conflictPreview.ConflictRecords);
        Assert.Equal("same-record", Assert.Single(conflictPreview.Conflicts).RecordId);
        Assert.Equal(new SanitizedImportExpectedChanges(0, 0, 0, 0, 0, 0), conflictPreview.ExpectedChanges);
        Assert.False(result.Success);
        Assert.Equal("record_conflict", result.ErrorCode);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal("repository-a", RepositoryName(temp.DatabasePath));
    }

    [Theory]
    [InlineData("after_records")]
    [InlineData("after_origins")]
    [InlineData("after_graph")]
    public void Commit_InjectedFailureRollsBackRecordsGraphOriginsAndHistory(string failingCheckpoint)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider,
            checkpoint => { if (checkpoint == failingCheckpoint) throw new InvalidOperationException("synthetic"); });
        store.CreateSchema();
        var preview = store.Preview(GoldenBundle());

        var result = store.Commit(GoldenBundle(), preview.PreviewDigest!);

        Assert.False(result.Success);
        Assert.Equal("import_transaction_failed", result.ErrorCode);
        foreach (var table in new[] { "sanitized_import_history", "sanitized_import_records", "sanitized_import_origins", "sanitized_import_graph_nodes", "sanitized_import_graph_edges" })
            Assert.Equal(0L, Scalar(temp.DatabasePath, $"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public void Commit_AllFrozenCarriersPreservesExactGraphAndNeverWritesOwnerTables()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var bundle = AllCarrierBundle();
        var preview = store.Preview(bundle);

        var result = store.Commit(bundle, preview.PreviewDigest!);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(3L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal(3L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
        Assert.Equal("defined", Text(temp.DatabasePath, "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='instruction_analysis_run' AND source_id='123';"));
        Assert.Equal("defined", Text(temp.DatabasePath, "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='alert_evaluation';"));
        Assert.Equal("resolved", Text(temp.DatabasePath, "SELECT resolution_state FROM sanitized_import_graph_edges WHERE relation='evaluation';"));
        Assert.Equal("defined", Text(temp.DatabasePath, "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='session' AND source_id='session-1';"));
        Assert.Equal(AlertBytes(), (byte[])Blob(temp.DatabasePath, "SELECT canonical_json FROM sanitized_import_records WHERE record_type='alert_receipt';"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN ('alert_receipts','alert_evaluations','instruction_finding_handoffs');"));
    }

    [Fact]
    public void Commit_DifferentArchiveWithExactRecordAddsOriginButNotRecordOrGraph()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var first = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var second = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));

        Assert.True(store.Commit(first, store.Preview(first).PreviewDigest!).Success);
        var secondPreview = store.Preview(second);
        var result = store.Commit(second, secondPreview.PreviewDigest!);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(0, result.NewRecords);
        Assert.Equal(1, result.DuplicateRecords);
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_graph_edges;"));
    }

    [Fact]
    public void Commit_LaterExactDefinitionResolvesPreviouslyExternalEdge()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var alertOnly = AlertOnlyBundle();
        var repository = RepositoryBundle("session-1", "repository-a", new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));

        Assert.True(store.Commit(alertOnly, store.Preview(alertOnly).PreviewDigest!).Success);
        Assert.Equal("external", Text(temp.DatabasePath,
            "SELECT resolution_state FROM sanitized_import_graph_edges WHERE relation='session';"));

        var result = store.Commit(repository, store.Preview(repository).PreviewDigest!);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal("defined", Text(temp.DatabasePath,
            "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='session' AND source_id='session-1';"));
        Assert.Equal("resolved", Text(temp.DatabasePath,
            "SELECT resolution_state FROM sanitized_import_graph_edges WHERE relation='session';"));
    }

    [Theory]
    [InlineData(SanitizedExportDependencyDisposition.External, SanitizedExportDependencyDisposition.Required)]
    [InlineData(SanitizedExportDependencyDisposition.Required, SanitizedExportDependencyDisposition.External)]
    public void Commit_UnresolvedStateUsesMissingOverExternalAcrossArchives(
        SanitizedExportDependencyDisposition firstDisposition,
        SanitizedExportDependencyDisposition secondDisposition)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var first = RepositoryBundleWithDependency(firstDisposition, new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var second = RepositoryBundleWithDependency(secondDisposition, new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));

        Assert.True(store.Commit(first, store.Preview(first).PreviewDigest!).Success);
        var preview = store.Preview(second);
        var result = store.Commit(second, preview.PreviewDigest!);

        Assert.Equal("missing", Assert.Single(preview.UnresolvedReferences,
            item => item.NodeKind == "record:alert_receipt" && item.SourceId == "dependency-record").State);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal("missing", Text(temp.DatabasePath,
            "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='record:alert_receipt' AND source_id='dependency-record';"));
    }

    [Fact]
    public void GraphProjection_RejectsNodeLimitBeforeAddingAnExtraNode()
    {
        var missing = Enumerable.Range(0, SanitizedImportLimits.MaximumGraphNodes + 1)
            .Select(index => new SanitizedImportUnresolved("record:alert_receipt", $"missing-{index}", "external"))
            .ToArray();

        Assert.Throws<SanitizedImportGraphLimitException>(() => SanitizedImportGraphProjector.Project([], missing));
    }

    [Fact]
    public void Commit_WhenImportedStateChangesAfterPreviewRejectsWithoutSecondHistory()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var previewed = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var intervening = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));
        var stalePreview = store.Preview(previewed);
        Assert.True(store.Commit(intervening, store.Preview(intervening).PreviewDigest!).Success);

        var result = store.Commit(previewed, stalePreview.PreviewDigest!);

        Assert.False(result.Success);
        Assert.Equal("preview_changed", result.ErrorCode);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
    }

    [Fact]
    public void Preview_PropagatesStrictArchiveInspectorFailuresWithoutImportWrites()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var bundle = GoldenBundle();
        var cases = new (byte[] Bytes, string Error)[]
        {
            (Repack(bundle, CompressionLevel.SmallestSize), "compression_not_allowed"),
            (Repack(bundle, CompressionLevel.NoCompression, externalAttributes: 32), "archive_attributes_invalid"),
            (Repack(bundle, CompressionLevel.NoCompression, manifestPrefix: " "), "manifest_not_canonical"),
            (Repack(bundle, CompressionLevel.NoCompression, manifestReplace: ("sanitized-evidence-bundle.v1", "sanitized-evidence-bundle.v2")), "schema_unsupported"),
            (Repack(bundle, CompressionLevel.NoCompression, mutateRecord: true), "checksum_mismatch"),
        };

        foreach (var item in cases)
        {
            var preview = store.Preview(item.Bytes);
            Assert.False(preview.Success);
            Assert.Equal(item.Error, preview.ErrorCode);
        }
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
    }

    internal static byte[] RepositoryBundle(string recordId, string repositoryName, DateTimeOffset? createdAt = null)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var record = new SanitizedExportRecord(
            $"repository-metadata/{recordId}.json", "repository_metadata_projection", recordId,
            recordId, "trace-a", "github-copilot-cli", repositoryName, "sample-workspace", "sample-snapshot",
            observedAt, [], [], "partial", "not_captured", "retained_by_policy");
        record = record with { CanonicalBytes = Encoding.UTF8.GetBytes(
            $"{{\"schema_version\":\"repository-metadata-projection.v1\",\"record_id\":{JsonSerializer.Serialize(recordId)},\"session_id\":{JsonSerializer.Serialize(recordId)},\"trace_id\":\"trace-a\",\"source_surface\":\"github-copilot-cli\",\"repository_name\":{JsonSerializer.Serialize(repositoryName)},\"workspace_label\":\"sample-workspace\",\"repo_snapshot\":\"sample-snapshot\",\"observed_at\":\"2026-07-22T00:00:00.0000000Z\",\"completeness\":\"partial\",\"content_state\":\"not_captured\",\"retention_state\":\"retained_by_policy\"}}") };
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-import-test", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], [record],
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(createdAt ?? observedAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] AllCarrierBundle()
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var repository = new SanitizedExportRecord(
            "repository-metadata/session-1.json", "repository_metadata_projection", "session-1",
            "session-1", "trace-1", "github-copilot", "sample-repository", "sample-workspace", "sample-snapshot",
            observedAt, [], [], "partial", "not_captured", "retained_by_policy");
        repository = repository with { CanonicalBytes = RepositoryMetadataProjectionV1.Serialize(
            "session-1", "session-1", "trace-1", "github-copilot", "sample-repository", "sample-workspace",
            "sample-snapshot", observedAt, "partial", "not_captured", "retained_by_policy") };
        var instruction = new SanitizedExportRecord(
            "instruction-findings/123.json", "instruction_finding_handoff", "123", null, null, null,
            null, null, null, observedAt, InstructionBytes(), []);
        var alertBytes = AlertBytes();
        var alert = AlertReceiptConsumerV1.Validate(alertBytes);
        var receipt = new SanitizedExportRecord(
            $"alert-receipts/{alert.AlertId}.json", "alert_receipt", alert.AlertId, alert.SessionId,
            alert.TraceId, alert.SourceSurface, null, null, null, alert.LastObservedAt, alertBytes, []);
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-all-carriers", "local-monitor-test", [new("github-copilot", "1.2.3")],
            [repository, instruction, receipt], new("available", "available", "unavailable", "unavailable", "unavailable"));
        foreach (var record in snapshot.Records)
            Assert.True(SanitizedExportProducerValidator.Validate(record) is null, $"Invalid {record.RecordType} carrier.");
        var result = new SanitizedExportService().Create(new(observedAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] AlertOnlyBundle()
    {
        var alertBytes = AlertBytes();
        var alert = AlertReceiptConsumerV1.Validate(alertBytes);
        var receipt = new SanitizedExportRecord(
            $"alert-receipts/{alert.AlertId}.json", "alert_receipt", alert.AlertId, alert.SessionId,
            alert.TraceId, alert.SourceSurface, null, null, null, alert.LastObservedAt, alertBytes, []);
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-alert-only", "local-monitor-test", [new(alert.SourceSurface, "1.2.3")],
            [receipt], new("missing", "available", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(alert.LastObservedAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] RepositoryBundleWithDependency(
        SanitizedExportDependencyDisposition disposition,
        DateTimeOffset createdAt)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var record = new SanitizedExportRecord(
            "repository-metadata/dependency-source.json", "repository_metadata_projection", "dependency-source",
            "dependency-source", "trace-a", "github-copilot-cli", "repository-a", "sample-workspace", "sample-snapshot",
            observedAt, RepositoryMetadataProjectionV1.Serialize(
                "dependency-source", "dependency-source", "trace-a", "github-copilot-cli", "repository-a",
                "sample-workspace", "sample-snapshot", observedAt, "partial", "not_captured", "retained_by_policy"),
            [new("alert_receipt", "dependency-record", disposition)], "partial", "not_captured", "retained_by_policy");
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-dependency", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], [record],
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(createdAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] InstructionBytes()
    {
        var encoded = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "specifications", "contracts",
            "instruction-findings", "v1", "instruction-finding-handoff.canonical.base64")).Trim();
        return Convert.FromBase64String(encoded);
    }

    private static byte[] AlertBytes() => SanitizedExportAlertFixture.Bytes();

    private static byte[] Repack(
        byte[] sourceBytes,
        CompressionLevel compression,
        int externalAttributes = 0,
        string manifestPrefix = "",
        (string Old, string New)? manifestReplace = null,
        bool mutateRecord = false)
    {
        using var source = new ZipArchive(new MemoryStream(sourceBytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var sourceEntry in source.Entries)
            {
                var entry = target.CreateEntry(sourceEntry.FullName, compression);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = externalAttributes;
                using var input = sourceEntry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var bytes = buffer.ToArray();
                if (sourceEntry.FullName == "manifest.json")
                {
                    var text = manifestPrefix + Encoding.UTF8.GetString(bytes);
                    if (manifestReplace is { } replace) text = text.Replace(replace.Old, replace.New, StringComparison.Ordinal);
                    bytes = Encoding.UTF8.GetBytes(text);
                }
                else if (mutateRecord)
                {
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
                        .Replace("sample-repository", "sample-repositorx", StringComparison.Ordinal));
                }
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Text(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static string RepositoryName(string databasePath)
    {
        var bytes = (byte[])Blob(databasePath, "SELECT canonical_json FROM sanitized_import_records;");
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("repository_name").GetString()!;
    }

    private static object Blob(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    internal static byte[] GoldenBundle() => File.ReadAllBytes(Path.Combine(
        FindRepositoryRoot(), "tests", "CopilotAgentObservability.LocalMonitor.Tests",
        "TestData", "SanitizedExport", "sanitized-evidence.v1.zip"));

    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
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
        Assert.Equal(1, preview.EligibleRecords);
        Assert.Equal(1, preview.NewRecords);
        Assert.Equal(0, preview.UpdatedRecords);
        Assert.Equal(0, preview.SkippedRecords);
        Assert.Equal(0, preview.RejectedRecords);
        Assert.Equal(0, preview.DuplicateRecords);
        Assert.Equal(0, preview.ConflictRecords);
        Assert.Equal(0, preview.GraphStateUpdates);
        Assert.Equal(0, preview.ManifestDeclarationCount);
        Assert.Empty(preview.ManifestDeclarations);
        Assert.Equal(0, preview.UnresolvedReferenceCount);
        Assert.Empty(preview.UnresolvedReferences);
        AssertCountInvariant(preview.EligibleRecords, preview.NewRecords, preview.UpdatedRecords,
            preview.SkippedRecords, preview.RejectedRecords, preview.DuplicateRecords, preview.ConflictRecords);
        Assert.True(preview.CanCommit);
        Assert.Equal(1, preview.ExpectedChanges.Records);
        Assert.Equal(1, preview.ExpectedChanges.Origins);
        Assert.True(preview.ExpectedChanges.GraphNodes >= 3);
        Assert.Equal(0, preview.ExpectedChanges.GraphDeclarations);
        Assert.Equal(0, preview.ExpectedChanges.GraphStateUpdates);
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
        Assert.Equal(1, first.EligibleRecords);
        Assert.Equal(1, first.NewRecords);
        Assert.Equal(0, first.UpdatedRecords);
        Assert.Equal(0, first.SkippedRecords);
        Assert.Equal(0, first.RejectedRecords);
        Assert.Equal(0, first.DuplicateRecords);
        Assert.Equal(0, first.ConflictRecords);
        AssertCountInvariant(first.EligibleRecords, first.NewRecords, first.UpdatedRecords,
            first.SkippedRecords, first.RejectedRecords, first.DuplicateRecords, first.ConflictRecords);
        Assert.True(replay.Success, replay.ErrorCode);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.ImportId, replay.ImportId);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
        Assert.Equal(beforeRetention, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM retention_items;"));
        var history = Assert.Single(store.ListHistory(100).Items);
        Assert.Equal(first.EligibleRecords, history.EligibleRecords);
        Assert.Equal(first.NewRecords, history.NewRecords);
        Assert.Equal(first.UpdatedRecords, history.UpdatedRecords);
        Assert.Equal(first.SkippedRecords, history.SkippedRecords);
        Assert.Equal(first.RejectedRecords, history.RejectedRecords);
        Assert.Equal(first.DuplicateRecords, history.DuplicateRecords);
        Assert.Equal(first.ConflictRecords, history.ConflictRecords);
        Assert.Equal(first.GraphStateUpdates, history.GraphStateUpdates);
        AssertCountInvariant(history.EligibleRecords, history.NewRecords, history.UpdatedRecords,
            history.SkippedRecords, history.RejectedRecords, history.DuplicateRecords, history.ConflictRecords);

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
        Assert.Equal(1, conflictPreview.EligibleRecords);
        Assert.Equal(0, conflictPreview.NewRecords);
        Assert.Equal(0, conflictPreview.UpdatedRecords);
        Assert.Equal(0, conflictPreview.SkippedRecords);
        Assert.Equal(1, conflictPreview.RejectedRecords);
        Assert.Equal(0, conflictPreview.DuplicateRecords);
        Assert.Equal(1, conflictPreview.ConflictRecords);
        AssertCountInvariant(conflictPreview.EligibleRecords, conflictPreview.NewRecords, conflictPreview.UpdatedRecords,
            conflictPreview.SkippedRecords, conflictPreview.RejectedRecords, conflictPreview.DuplicateRecords, conflictPreview.ConflictRecords);
        Assert.Equal("same-record", Assert.Single(conflictPreview.Conflicts).RecordId);
        Assert.Equal(new SanitizedImportExpectedChanges(0, 0, 0, 0, 0, 0, 0, 0), conflictPreview.ExpectedChanges);
        Assert.False(result.Success);
        Assert.Equal("record_conflict", result.ErrorCode);
        Assert.Equal(1, result.EligibleRecords);
        Assert.Equal(0, result.NewRecords);
        Assert.Equal(0, result.UpdatedRecords);
        Assert.Equal(0, result.SkippedRecords);
        Assert.Equal(1, result.RejectedRecords);
        Assert.Equal(0, result.DuplicateRecords);
        Assert.Equal(1, result.ConflictRecords);
        AssertCountInvariant(result.EligibleRecords, result.NewRecords, result.UpdatedRecords,
            result.SkippedRecords, result.RejectedRecords, result.DuplicateRecords, result.ConflictRecords);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal("repository-a", RepositoryName(temp.DatabasePath));
    }

    [Fact]
    public void Preview_MixedRecordClassificationsPreserveExactCountAlgebraWhenCommitIsBlocked()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var baseline = RepositoryBundleMany(
            [("conflict-record", "repository-a"), ("duplicate-record", "repository-d")],
            new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var candidate = RepositoryBundleMany(
            [("conflict-record", "repository-b"), ("duplicate-record", "repository-d"), ("new-record", "repository-n")],
            new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));
        Assert.True(store.Commit(baseline, store.Preview(baseline).PreviewDigest!).Success);

        var preview = store.Preview(candidate);
        var result = store.Commit(candidate, preview.PreviewDigest!);

        Assert.False(preview.CanCommit);
        Assert.Equal((3, 1, 0, 1, 1, 1, 1),
            (preview.EligibleRecords, preview.NewRecords, preview.UpdatedRecords, preview.SkippedRecords,
                preview.RejectedRecords, preview.DuplicateRecords, preview.ConflictRecords));
        AssertCountInvariant(preview.EligibleRecords, preview.NewRecords, preview.UpdatedRecords,
            preview.SkippedRecords, preview.RejectedRecords, preview.DuplicateRecords, preview.ConflictRecords);
        Assert.Equal(new SanitizedImportExpectedChanges(0, 0, 0, 0, 0, 0, 0, 0), preview.ExpectedChanges);
        Assert.False(result.Success);
        Assert.Equal((3, 1, 0, 1, 1, 1, 1),
            (result.EligibleRecords, result.NewRecords, result.UpdatedRecords, result.SkippedRecords,
                result.RejectedRecords, result.DuplicateRecords, result.ConflictRecords));
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
        foreach (var table in new[] { "sanitized_import_history", "sanitized_import_records", "sanitized_import_origins", "sanitized_import_graph_nodes", "sanitized_import_graph_declarations", "sanitized_import_graph_edges" })
            Assert.Equal(0L, Scalar(temp.DatabasePath, $"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public void Commit_UnrelatedOwnedForeignKeyCorruptionFailsIntegrityWithoutWritesOrRepair()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        Execute(temp.DatabasePath, $"""
            INSERT INTO sanitized_import_graph_nodes(
                local_node_id,node_kind,source_id,state,defining_record_local_id,first_import_id)
            VALUES('{new string('e', 64)}','unrelated','orphan','unresolved',NULL,'{new string('b', 64)}');
            """);
        var bundle = GoldenBundle();

        var result = store.Commit(bundle, new string('a', 64));

        Assert.False(result.Success);
        Assert.Equal("import_integrity_failed", result.ErrorCode);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_graph_nodes;"));
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
        Assert.Equal(1, result.GraphDeclarations);
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
    public void GraphProjection_AlertEvidenceUsesFullTupleAndScopedChildIdentity()
    {
        var observedAt = new DateTimeOffset(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);
        var bytes = SanitizedExportAlertFixture.Bytes(
            new(AlertEvidenceKind.Event, "shared-evidence", "session-1", "trace-1", "span-1", null, "shared-event", null, observedAt),
            new(AlertEvidenceKind.Event, "shared-evidence", "session-1", "trace-1", "span-2", null, "shared-event", null, observedAt.AddTicks(1)));
        using var document = JsonDocument.Parse(bytes);
        var recordId = document.RootElement.GetProperty("alert_id").GetString()!;
        var record = new SanitizedImportRecord(
            $"alert-receipts/{recordId}.json", "alert_receipt", recordId,
            SanitizedImportIdentity.Hash("sanitized-import-record.v1", "alert_receipt", recordId),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), bytes);

        var graph = SanitizedImportGraphProjector.Project([record], []);

        var evidenceNodes = graph.Nodes.Where(node => node.NodeKind == "alert_evidence").ToArray();
        Assert.Equal(2, evidenceNodes.Length);
        var expectedEvidenceIdentities = document.RootElement.GetProperty("evidence").EnumerateArray()
            .Select(item => item.GetRawText()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedEvidenceIdentities, evidenceNodes.Select(node => node.SourceId).Order(StringComparer.Ordinal));
        Assert.All(evidenceNodes, node =>
        {
            Assert.Contains("\"kind\":\"event\"", node.SourceId, StringComparison.Ordinal);
            Assert.Contains("\"evidence_id\":\"shared-evidence\"", node.SourceId, StringComparison.Ordinal);
            Assert.Contains("\"session_id\":\"session-1\"", node.SourceId, StringComparison.Ordinal);
            Assert.Contains("\"trace_id\":\"trace-1\"", node.SourceId, StringComparison.Ordinal);
            Assert.Contains("\"turn_id\":null", node.SourceId, StringComparison.Ordinal);
            Assert.Contains("\"event_id\":\"shared-event\"", node.SourceId, StringComparison.Ordinal);
            Assert.Contains("\"tool_call_id\":null", node.SourceId, StringComparison.Ordinal);
            Assert.Contains("\"observed_at\":", node.SourceId, StringComparison.Ordinal);
        });
        var eventNodes = graph.Nodes.Where(node => node.NodeKind == "alert_evidence_event").ToArray();
        Assert.Equal(2, eventNodes.Length);
        Assert.Equal(expectedEvidenceIdentities, eventNodes.Select(node => node.SourceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GraphProjection_InstructionOpaqueRefsNeverShareRepositoryIdentityNamespace()
    {
        var instructionBytes = InstructionBytes();
        using var document = JsonDocument.Parse(instructionBytes);
        var opaqueTrace = document.RootElement.GetProperty("findings")[0].GetProperty("anchor_trace_id").GetString()!;
        var repositoryBytes = RepositoryMetadataProjectionV1.Serialize(
            "repository-record", "actual-session", opaqueTrace, "github-copilot", "repository", "workspace",
            "snapshot", new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), "partial", "not_captured", "retained_by_policy");
        var records = new[]
        {
            ImportRecord("repository_metadata_projection", "repository-record", "repository-metadata/repository-record.json", repositoryBytes),
            ImportRecord("instruction_finding_handoff", "123", "instruction-findings/123.json", instructionBytes),
        };

        var graph = SanitizedImportGraphProjector.Project(records, []);
        var nodes = graph.Nodes.ToDictionary(node => node.LocalNodeId, StringComparer.Ordinal);

        Assert.Contains(graph.Nodes, node => node.NodeKind == "trace" && node.SourceId == opaqueTrace && node.State == "defined");
        Assert.Contains(graph.Nodes, node => node.NodeKind == "instruction_anchor_trace" && node.SourceId == opaqueTrace && node.State == "external");
        Assert.Contains(graph.Nodes, node => node.NodeKind == "instruction_evidence_trace" && node.SourceId == opaqueTrace && node.State == "external");
        Assert.Contains(graph.Nodes, node => node.NodeKind == "instruction_provenance_trace" && node.SourceId == opaqueTrace && node.State == "external");
        Assert.All(graph.Edges.Where(edge => edge.Relation is "anchor_trace" or "evidence_session" or "evidence_trace"
                or "evidence_span" or "evidence_turn" or "provenance_trace"),
            edge => Assert.StartsWith("instruction_", nodes[edge.TargetNodeId].NodeKind, StringComparison.Ordinal));
    }

    [Fact]
    public void GraphProjection_EdgeOrdinalsAreIndependentPerRecord()
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var repositoryBytes = RepositoryMetadataProjectionV1.Serialize(
            "repository-record", "session-1", "trace-1", "github-copilot", "repository", "workspace",
            "snapshot", observedAt, "partial", "not_captured", "retained_by_policy");
        var alertBytes = AlertBytes();
        using var alert = JsonDocument.Parse(alertBytes);
        var records = new[]
        {
            ImportRecord("repository_metadata_projection", "repository-record", "repository-metadata/repository-record.json", repositoryBytes),
            ImportRecord("instruction_finding_handoff", "123", "instruction-findings/123.json", InstructionBytes()),
            ImportRecord("alert_receipt", alert.RootElement.GetProperty("alert_id").GetString()!, "alert-receipts/receipt.json", alertBytes),
        };

        var graph = SanitizedImportGraphProjector.Project(records, []);

        foreach (var group in graph.Edges.GroupBy(edge => edge.SourceRecordLocalId))
            Assert.Equal(Enumerable.Range(0, group.Count()), group.OrderBy(edge => edge.Ordinal).Select(edge => edge.Ordinal));
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
        Assert.Equal(1, result.EligibleRecords);
        Assert.Equal(0, result.NewRecords);
        Assert.Equal(0, result.UpdatedRecords);
        Assert.Equal(1, result.SkippedRecords);
        Assert.Equal(0, result.RejectedRecords);
        Assert.Equal(1, result.DuplicateRecords);
        Assert.Equal(0, result.ConflictRecords);
        AssertCountInvariant(result.EligibleRecords, result.NewRecords, result.UpdatedRecords,
            result.SkippedRecords, result.RejectedRecords, result.DuplicateRecords, result.ConflictRecords);
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_graph_edges;"));
    }

    [Theory]
    [InlineData("DELETE FROM sanitized_import_graph_edges WHERE rowid=(SELECT MIN(rowid) FROM sanitized_import_graph_edges);")]
    [InlineData("UPDATE sanitized_import_graph_edges SET resolution_state='missing' WHERE rowid=(SELECT MIN(rowid) FROM sanitized_import_graph_edges);")]
    [InlineData("UPDATE sanitized_import_graph_edges SET provenance_json='{}' WHERE rowid=(SELECT MIN(rowid) FROM sanitized_import_graph_edges);")]
    public void Commit_DuplicateRecordArchiveRejectsCorruptPriorGraphWithoutLaunderingIt(string corruptionSql)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var first = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var second = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));
        Assert.True(store.Commit(first, store.Preview(first).PreviewDigest!).Success);
        var secondPreview = store.Preview(second);
        Execute(temp.DatabasePath, corruptionSql);

        var corruptPreview = store.Preview(second);
        var result = store.Commit(second, secondPreview.PreviewDigest!);

        Assert.False(corruptPreview.Success);
        Assert.Equal("import_integrity_failed", corruptPreview.ErrorCode);
        Assert.False(result.Success);
        Assert.Equal("import_integrity_failed", result.ErrorCode);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
    }

    [Fact]
    public void Preview_ConflictClassificationRejectsCorruptPriorRecordOwnerReceipt()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var first = RepositoryBundle("shared-record", "repository-a");
        Assert.True(store.Commit(first, store.Preview(first).PreviewDigest!).Success);
        Execute(temp.DatabasePath, "UPDATE sanitized_import_history SET graph_edges=graph_edges+1;");

        var preview = store.Preview(RepositoryBundle("shared-record", "repository-b",
            new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero)));

        Assert.False(preview.Success);
        Assert.Equal("import_integrity_failed", preview.ErrorCode);
    }

    [Fact]
    public void Preview_GraphResolutionRejectsCorruptPriorNodeAndDefinitionOwnerReceipts()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var definition = RepositoryBundle("session-1", "repository-a");
        Assert.True(store.Commit(definition, store.Preview(definition).PreviewDigest!).Success);
        Execute(temp.DatabasePath, "UPDATE sanitized_import_history SET graph_nodes=graph_nodes+1;");

        var preview = store.Preview(AlertOnlyBundle());

        Assert.False(preview.Success);
        Assert.Equal("import_integrity_failed", preview.ErrorCode);
    }

    [Fact]
    public void Preview_GraphPromotionRejectsCorruptUnresolvedNodeOwnerReceipt()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var unresolved = AlertOnlyBundle();
        Assert.True(store.Commit(unresolved, store.Preview(unresolved).PreviewDigest!).Success);
        Execute(temp.DatabasePath, "UPDATE sanitized_import_history SET graph_edges=graph_edges+1;");

        var preview = store.Preview(RepositoryBundle("session-1", "repository-a",
            new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero)));

        Assert.False(preview.Success);
        Assert.Equal("import_integrity_failed", preview.ErrorCode);
    }

    [Fact]
    public void Commit_DuplicateRecordReplayRejectsCorruptFirstImportReceipt()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var first = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var second = RepositoryBundle("shared-record", "repository-a", new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));
        Assert.True(store.Commit(first, store.Preview(first).PreviewDigest!).Success);
        var secondPreview = store.Preview(second);
        Assert.True(store.Commit(second, secondPreview.PreviewDigest!).Success);
        Execute(temp.DatabasePath, """
            UPDATE sanitized_import_history
            SET graph_edges=graph_edges+1
            WHERE import_id=(SELECT first_import_id FROM sanitized_import_records LIMIT 1);
            """);

        var replay = store.Commit(second, secondPreview.PreviewDigest!);

        Assert.False(replay.Success);
        Assert.Equal("import_integrity_failed", replay.ErrorCode);
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
    }

    [Fact]
    public void Commit_LaterExactDefinitionPromotesGlobalNodeWithoutRewritingEarlierEvidence()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var alertOnly = AlertOnlyBundle();
        var repository = RepositoryBundle("session-1", "repository-a", new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));
        var alertPreview = store.Preview(alertOnly);

        Assert.True(store.Commit(alertOnly, alertPreview.PreviewDigest!).Success);
        Assert.Equal("external", Text(temp.DatabasePath,
            "SELECT resolution_state FROM sanitized_import_graph_edges WHERE relation='session';"));

        var repositoryPreview = store.Preview(repository);
        Assert.Equal(1, repositoryPreview.GraphStateUpdates);
        Assert.Equal(1, repositoryPreview.ExpectedChanges.GraphStateUpdates);
        var result = store.Commit(repository, repositoryPreview.PreviewDigest!);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(1, result.GraphStateUpdates);
        Assert.Equal(1, store.GetHistory(result.ImportId!)!.GraphStateUpdates);
        Assert.Equal("defined", Text(temp.DatabasePath,
            "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='session' AND source_id='session-1';"));
        Assert.Equal("external", Text(temp.DatabasePath,
            "SELECT resolution_state FROM sanitized_import_graph_edges WHERE relation='session';"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, """
            SELECT COUNT(*)
            FROM sanitized_import_graph_declarations d
            JOIN sanitized_import_graph_nodes n ON n.local_node_id=d.local_node_id
            WHERE n.node_kind='session' AND n.source_id='session-1';
            """));
        var repositoryReplay = store.Commit(repository, repositoryPreview.PreviewDigest!);
        Assert.True(repositoryReplay.Success, repositoryReplay.ErrorCode);
        Assert.True(repositoryReplay.IdempotentReplay);
        var replay = store.Commit(alertOnly, alertPreview.PreviewDigest!);
        Assert.True(replay.Success, replay.ErrorCode);
        Assert.True(replay.IdempotentReplay);
    }

    [Theory]
    [InlineData("UPDATE sanitized_import_graph_nodes SET state='unresolved',defining_record_local_id=NULL WHERE node_kind='session' AND source_id='session-1';")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE sanitized_import_graph_nodes SET state='unresolved' WHERE node_kind='session' AND source_id='session-1';")]
    public void PreviewAndCommit_RejectPromotedNodeRollbackWithoutAdoptingOrRepairingIt(string corruptionSql)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var unresolved = AlertOnlyBundle();
        var definition = RepositoryBundle("session-1", "repository-a",
            new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero));
        Assert.True(store.Commit(unresolved, store.Preview(unresolved).PreviewDigest!).Success);
        Assert.True(store.Commit(definition, store.Preview(definition).PreviewDigest!).Success);
        Execute(temp.DatabasePath, corruptionSql);
        var referencing = AlertOnlyBundle("rollback-probe");

        var preview = store.Preview(referencing);
        var commit = store.Commit(referencing, new string('a', 64));

        Assert.False(preview.Success);
        Assert.Equal("import_integrity_failed", preview.ErrorCode);
        Assert.False(commit.Success);
        Assert.Equal("import_integrity_failed", commit.ErrorCode);
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(2L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
        Assert.Equal("unresolved", Text(temp.DatabasePath,
            "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='session' AND source_id='session-1';"));
    }

    [Fact]
    public void PreviewAndCommit_RejectMissingEdgeFreeOwnedNodeWithoutRecreatingIt()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var original = EdgeFreeRepositoryBundle("edge-free-record");
        Assert.True(store.Commit(original, store.Preview(original).PreviewDigest!).Success);
        Execute(temp.DatabasePath, "DELETE FROM sanitized_import_graph_nodes;");
        var referencing = RepositoryBundleWithDependency(
            SanitizedExportDependencyDisposition.Required,
            new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero),
            "repository_metadata_projection",
            "edge-free-record");

        var preview = store.Preview(referencing);
        var commit = store.Commit(referencing, new string('a', 64));

        Assert.False(preview.Success);
        Assert.Equal("import_integrity_failed", preview.ErrorCode);
        Assert.False(commit.Success);
        Assert.Equal("import_integrity_failed", commit.ErrorCode);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_graph_nodes;"));
    }

    [Fact]
    public void Preview_DoesNotResolveAgainstAStoredDefinedFlagWithoutItsExactDefinition()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var existing = AllCarrierBundle();
        Assert.True(store.Commit(existing, store.Preview(existing).PreviewDigest!).Success);
        Execute(temp.DatabasePath, """
            UPDATE sanitized_import_graph_nodes
            SET defining_record_local_id=(
                SELECT local_record_id FROM sanitized_import_records
                WHERE record_type='instruction_finding_handoff')
            WHERE node_kind='session' AND source_id='session-1';
            """);

        var preview = store.Preview(AlertOnlyBundle());

        Assert.False(preview.Success);
        Assert.Equal("import_integrity_failed", preview.ErrorCode);
    }

    [Theory]
    [InlineData(SanitizedExportDependencyDisposition.External, SanitizedExportDependencyDisposition.Required)]
    [InlineData(SanitizedExportDependencyDisposition.Required, SanitizedExportDependencyDisposition.External)]
    public void Commit_UnresolvedDeclarationsRemainExactAcrossArchives(
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
        var expectedSecondState = secondDisposition == SanitizedExportDependencyDisposition.Required ? "missing" : "external";

        Assert.Equal(1, preview.ManifestDeclarationCount);
        Assert.Equal(expectedSecondState, Assert.Single(preview.ManifestDeclarations,
            item => item.NodeKind == "record:alert_receipt" && item.SourceId == "dependency-record").State);
        Assert.Equal(expectedSecondState, Assert.Single(preview.UnresolvedReferences,
            item => item.NodeKind == "record:alert_receipt" && item.SourceId == "dependency-record").State);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal("unresolved", Text(temp.DatabasePath,
            "SELECT state FROM sanitized_import_graph_nodes WHERE node_kind='record:alert_receipt' AND source_id='dependency-record';"));
        Assert.Equal(2L, Scalar(temp.DatabasePath, """
            SELECT COUNT(*)
            FROM sanitized_import_graph_declarations d
            JOIN sanitized_import_graph_nodes n ON n.local_node_id=d.local_node_id
            WHERE n.node_kind='record:alert_receipt' AND n.source_id='dependency-record';
            """));
        Assert.Equal(expectedSecondState, Text(temp.DatabasePath, $"""
            SELECT d.declared_state
            FROM sanitized_import_graph_declarations d
            JOIN sanitized_import_graph_nodes n ON n.local_node_id=d.local_node_id
            WHERE d.import_id='{preview.ArchiveSha256}'
              AND n.node_kind='record:alert_receipt' AND n.source_id='dependency-record';
            """));
    }

    [Fact]
    public void Preview_ReportsManifestDeclarationSeparatelyWhenDestinationAlreadyDefinesIt()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var definition = RepositoryBundle("dependency-record", "repository-a",
            new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        Assert.True(store.Commit(definition, store.Preview(definition).PreviewDigest!).Success);
        var declared = RepositoryBundleWithDependency(
            SanitizedExportDependencyDisposition.Required,
            new(2026, 7, 22, 0, 0, 1, TimeSpan.Zero),
            "repository_metadata_projection");

        var preview = store.Preview(declared);
        var result = store.Commit(declared, preview.PreviewDigest!);

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(1, preview.ManifestDeclarationCount);
        Assert.Equal("missing", Assert.Single(preview.ManifestDeclarations,
            item => item.NodeKind == "record:repository_metadata_projection"
                && item.SourceId == "dependency-record").State);
        Assert.Equal(0, preview.UnresolvedReferenceCount);
        Assert.Empty(preview.UnresolvedReferences);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(1L, Scalar(temp.DatabasePath, $"""
            SELECT COUNT(*)
            FROM sanitized_import_graph_declarations
            WHERE import_id='{preview.ArchiveSha256}' AND declared_state='missing';
            """));
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
    public void Commit_InvalidArchiveFailsBeforeDatabaseOpen()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "must-not-exist.sqlite");
        var store = new SqliteSanitizedImportStore(database, temp.TimeProvider);

        var result = store.Commit([0x01, 0x02, 0x03], new string('a', 64));

        Assert.False(result.Success);
        Assert.Equal("archive_invalid", result.ErrorCode);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public void Commit_FreshTargetCreatesHistoricalThenSanitizedSchemaAndReceiptInOneTransaction()
    {
        using var temp = new MonitorTempDirectory();
        var archive = GoldenBundle();
        var previewDatabase = Path.Combine(temp.Path, "preview.sqlite");
        var targetDatabase = Path.Combine(temp.Path, "target.sqlite");
        var previewStore = new SqliteSanitizedImportStore(previewDatabase, temp.TimeProvider);
        previewStore.CreateSchema();
        var preview = previewStore.Preview(archive);

        var result = new SqliteSanitizedImportStore(targetDatabase, temp.TimeProvider)
            .Commit(archive, preview.PreviewDigest!);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(1L, Scalar(targetDatabase,
            "SELECT version FROM schema_version WHERE component='historical_import';"));
        Assert.Equal(1L, Scalar(targetDatabase,
            "SELECT version FROM schema_version WHERE component='sanitized_import';"));
        Assert.Equal(1L, Scalar(targetDatabase, "SELECT COUNT(*) FROM sanitized_import_history;"));
    }

    [Theory]
    [InlineData("after_records")]
    [InlineData("after_origins")]
    [InlineData("after_graph")]
    public void Commit_FreshTargetFailureRollsBackSchemaVersionAndImportRows(string failingCheckpoint)
    {
        using var temp = new MonitorTempDirectory();
        var archive = GoldenBundle();
        var previewDatabase = Path.Combine(temp.Path, "preview.sqlite");
        var targetDatabase = Path.Combine(temp.Path, "target.sqlite");
        var previewStore = new SqliteSanitizedImportStore(previewDatabase, temp.TimeProvider);
        previewStore.CreateSchema();
        var preview = previewStore.Preview(archive);
        var store = new SqliteSanitizedImportStore(targetDatabase, temp.TimeProvider,
            checkpoint => { if (checkpoint == failingCheckpoint) throw new InvalidOperationException("synthetic"); });

        var result = store.Commit(archive, preview.PreviewDigest!);

        Assert.False(result.Success);
        Assert.Equal("import_transaction_failed", result.ErrorCode);
        Assert.Equal(0L, Scalar(targetDatabase, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name='schema_version' OR name LIKE 'sanitized_import_%' OR tbl_name LIKE 'sanitized_import_%';
            """));
    }

    [Theory]
    [InlineData("after_records")]
    [InlineData("after_origins")]
    [InlineData("after_graph")]
    public void Commit_PreIssue86FailurePreservesHistoricalRowsAndRollsBackSanitizedComponent(string failingCheckpoint)
    {
        using var temp = new MonitorTempDirectory();
        var archive = GoldenBundle();
        var previewDatabase = Path.Combine(temp.Path, "preview.sqlite");
        var targetDatabase = Path.Combine(temp.Path, "pre-issue-86.sqlite");
        var previewStore = new SqliteSanitizedImportStore(previewDatabase, temp.TimeProvider);
        previewStore.CreateSchema();
        var preview = previewStore.Preview(archive);
        new SqliteHistoricalImportStore(targetDatabase).CreateSchema();
        Execute(targetDatabase, """
            INSERT INTO historical_import_previews(
                preview_id,preview_digest,snapshot_version,snapshot_digest,source_selection_id,
                private_selection_json,probe_json,candidate_batch_json,preview_json,eligible,expires_at,created_at)
            VALUES('historical-preview','digest','hsv_1','snapshot','selection',
                   NULL,NULL,NULL,'{}',0,'2026-07-23T01:00:00.0000000Z','2026-07-23T00:00:00.0000000Z');
            """);
        var store = new SqliteSanitizedImportStore(targetDatabase, temp.TimeProvider,
            checkpoint => { if (checkpoint == failingCheckpoint) throw new InvalidOperationException("synthetic"); });

        var result = store.Commit(archive, preview.PreviewDigest!);

        Assert.False(result.Success);
        Assert.Equal("import_transaction_failed", result.ErrorCode);
        Assert.Equal(1L, Scalar(targetDatabase,
            "SELECT version FROM schema_version WHERE component='historical_import';"));
        Assert.Equal(1L, Scalar(targetDatabase,
            "SELECT COUNT(*) FROM historical_import_previews WHERE preview_id='historical-preview';"));
        Assert.Equal(0L, Scalar(targetDatabase,
            "SELECT COUNT(*) FROM schema_version WHERE component='sanitized_import';"));
        Assert.Equal(0L, Scalar(targetDatabase, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name LIKE 'sanitized_import_%' OR tbl_name LIKE 'sanitized_import_%';
            """));
    }

    [Fact]
    public void Commit_FreshTargetStaleDigestRollsBackSchemaAndVersionStamp()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "target.sqlite");

        var result = new SqliteSanitizedImportStore(database, temp.TimeProvider)
            .Commit(GoldenBundle(), new string('b', 64));

        Assert.False(result.Success);
        Assert.Equal("preview_changed", result.ErrorCode);
        Assert.Equal(0L, Scalar(database, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name='schema_version' OR name LIKE 'sanitized_import_%' OR tbl_name LIKE 'sanitized_import_%';
            """));
    }

    [Fact]
    public void Commit_UsesOneCallerByteSnapshotAcrossPreflightAndTransaction()
    {
        using var temp = new MonitorTempDirectory();
        var archive = GoldenBundle();
        var expectedRead = SanitizedImportBundleReader.Read(archive);
        Assert.True(expectedRead.Success, expectedRead.ErrorCode);
        var expectedRecordBytes = Assert.Single(expectedRead.Bundle!.Records).CanonicalBytes.ToArray();
        var expectedArchiveSha256 = expectedRead.Bundle.ArchiveSha256;
        var previewStore = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        previewStore.CreateSchema();
        var preview = previewStore.Preview(archive);
        var mutated = false;
        var commitStore = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider, checkpoint =>
        {
            if (checkpoint != "after_archive_preflight" || mutated) return;
            Array.Fill(archive, (byte)0);
            mutated = true;
        });

        var result = commitStore.Commit(archive, preview.PreviewDigest!);

        Assert.True(mutated);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(expectedArchiveSha256, result.ImportId);
        Assert.Equal(expectedRecordBytes, (byte[])Blob(temp.DatabasePath,
            "SELECT canonical_json FROM sanitized_import_records;"));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
    }

    [Fact]
    public void Commit_CannotSwitchFromCapturedArchiveAToEqualLengthArchiveBInsideTransaction()
    {
        using var temp = new MonitorTempDirectory();
        var archiveA = RepositoryBundle("snapshot-record", "repository-a");
        var archiveB = RepositoryBundle("snapshot-record", "repository-b");
        Assert.Equal(archiveA.Length, archiveB.Length);
        var previewStore = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        previewStore.CreateSchema();
        var previewB = previewStore.Preview(archiveB);
        var mutated = false;
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider, checkpoint =>
        {
            if (checkpoint != "after_archive_preflight" || mutated) return;
            archiveB.CopyTo(archiveA, 0);
            mutated = true;
        });

        var result = store.Commit(archiveA, previewB.PreviewDigest!);

        Assert.True(mutated);
        Assert.False(result.Success);
        Assert.Equal("preview_changed", result.ErrorCode);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
    }

    [Theory]
    [InlineData("DELETE FROM sanitized_import_records;")]
    [InlineData("DELETE FROM sanitized_import_origins;")]
    [InlineData("DELETE FROM sanitized_import_graph_nodes;")]
    [InlineData("DELETE FROM sanitized_import_graph_edges;")]
    [InlineData("DELETE FROM sanitized_import_graph_declarations;")]
    [InlineData("UPDATE sanitized_import_records SET canonical_json=x'7B7D';")]
    [InlineData("UPDATE sanitized_import_records SET canonical_json='{}';")]
    [InlineData("UPDATE sanitized_import_records SET created_at='2026-07-23T00:00:00.0000000Z';")]
    [InlineData("UPDATE sanitized_import_graph_nodes SET source_id=source_id || '-corrupt' WHERE rowid=(SELECT MIN(rowid) FROM sanitized_import_graph_nodes);")]
    [InlineData("UPDATE sanitized_import_graph_nodes SET state='defined',defining_record_local_id=(SELECT MIN(local_record_id) FROM sanitized_import_records) WHERE rowid=(SELECT MIN(rowid) FROM sanitized_import_graph_nodes WHERE state='unresolved');")]
    [InlineData("UPDATE sanitized_import_history SET graph_edges=graph_edges+1;")]
    [InlineData("UPDATE sanitized_import_history SET imported_at='invalid';")]
    [InlineData("INSERT INTO sanitized_import_graph_nodes(local_node_id,node_kind,source_id,state,defining_record_local_id,first_import_id) SELECT 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff','corrupt_extra','extra','unresolved',NULL,import_id FROM sanitized_import_history LIMIT 1;")]
    [InlineData("INSERT INTO sanitized_import_graph_edges(local_edge_id,source_record_local_id,source_node_id,target_node_id,relation,edge_ordinal,resolution_state,provenance_json,first_import_id) SELECT 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',r.local_record_id,n.local_node_id,n.local_node_id,'corrupt_extra',999,'external','{}',h.import_id FROM sanitized_import_history h CROSS JOIN sanitized_import_records r CROSS JOIN sanitized_import_graph_nodes n LIMIT 1;")]
    [InlineData("INSERT INTO sanitized_import_graph_declarations(import_id,local_node_id,declared_state) SELECT h.import_id,n.local_node_id,'external' FROM sanitized_import_history h CROSS JOIN sanitized_import_graph_nodes n WHERE n.state='defined' ORDER BY n.local_node_id LIMIT 1;")]
    [InlineData("INSERT INTO sanitized_import_records(local_record_id,record_type,source_record_id,canonical_sha256,canonical_json,first_import_id,created_at) SELECT 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff','repository_metadata_projection','corrupt-extra','ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff',x'7B7D',import_id,'2026-07-22T00:00:00.0000000Z' FROM sanitized_import_history LIMIT 1; INSERT INTO sanitized_import_origins(import_id,local_record_id,entry_path,source_snapshot_id,source_local_monitor_version,source_created_at,imported_at) SELECT import_id,'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff','corrupt-extra.json',source_snapshot_id,source_local_monitor_version,imported_at,imported_at FROM sanitized_import_history LIMIT 1;")]
    public void Commit_ReplayRejectsIncompleteOrMismatchedOwnedStateWithoutRepair(string corruptionSql)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var bundle = AllCarrierBundle();
        var preview = store.Preview(bundle);
        Assert.True(store.Commit(bundle, preview.PreviewDigest!).Success);
        Execute(temp.DatabasePath, corruptionSql);

        var corruptPreview = store.Preview(bundle);
        var replay = store.Commit(bundle, preview.PreviewDigest!);

        Assert.False(corruptPreview.Success);
        Assert.Equal("import_integrity_failed", corruptPreview.ErrorCode);
        Assert.False(replay.Success);
        Assert.Equal("import_integrity_failed", replay.ErrorCode);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
    }

    [Fact]
    public void Commit_ReplayRejectsMissingHistoryFootprintWithoutRecreatingIt()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var bundle = AllCarrierBundle();
        var preview = store.Preview(bundle);
        Assert.True(store.Commit(bundle, preview.PreviewDigest!).Success);
        Execute(temp.DatabasePath, """
            DELETE FROM sanitized_import_graph_declarations;
            DELETE FROM sanitized_import_graph_edges;
            DELETE FROM sanitized_import_graph_nodes;
            DELETE FROM sanitized_import_origins;
            DELETE FROM sanitized_import_history;
            """);

        var corruptPreview = store.Preview(bundle);
        var replay = store.Commit(bundle, preview.PreviewDigest!);

        Assert.False(corruptPreview.Success);
        Assert.Equal("import_integrity_failed", corruptPreview.ErrorCode);
        Assert.False(replay.Success);
        Assert.Equal("import_integrity_failed", replay.ErrorCode);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.True(Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;") > 0);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_origins;"));
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

    private static byte[] EdgeFreeRepositoryBundle(string recordId)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var bytes = RepositoryMetadataProjectionV1.Serialize(
            recordId, null, null, "github-copilot-cli", "repository-a", "sample-workspace", "sample-snapshot",
            observedAt, "partial", "not_captured", "retained_by_policy");
        var record = new SanitizedExportRecord(
            $"repository-metadata/{recordId}.json", "repository_metadata_projection", recordId,
            null, null, "github-copilot-cli", "repository-a", "sample-workspace", "sample-snapshot",
            observedAt, bytes, [], "partial", "not_captured", "retained_by_policy");
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-edge-free", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], [record],
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(observedAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] RepositoryBundleMany(
        IReadOnlyList<(string Id, string Repository)> items,
        DateTimeOffset createdAt)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var records = items.Select(item => new SanitizedExportRecord(
            $"repository-metadata/{item.Id}.json",
            "repository_metadata_projection",
            item.Id,
            item.Id,
            $"trace-{item.Id}",
            "github-copilot-cli",
            item.Repository,
            "sample-workspace",
            "sample-snapshot",
            observedAt,
            RepositoryMetadataProjectionV1.Serialize(
                item.Id, item.Id, $"trace-{item.Id}", "github-copilot-cli", item.Repository,
                "sample-workspace", "sample-snapshot", observedAt, "partial", "not_captured", "retained_by_policy"),
            [],
            "partial",
            "not_captured",
            "retained_by_policy")).ToArray();
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-import-count-test", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], records,
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(createdAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] AllCarrierBundle()
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var repository = new SanitizedExportRecord(
            "repository-metadata/session-1.json", "repository_metadata_projection", "session-1",
            "session-1", "trace-1", "github-copilot", "sample-repository", "sample-workspace", "sample-snapshot",
            observedAt, [], [new("alert_receipt", "declared-missing", SanitizedExportDependencyDisposition.Required)],
            "partial", "not_captured", "retained_by_policy");
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

    private static byte[] AlertOnlyBundle(string? variant = null)
    {
        var alertBytes = variant is null
            ? AlertBytes()
            : SanitizedExportAlertFixture.Bytes(new AlertEvidenceReference(
                AlertEvidenceKind.Event,
                $"evidence-{variant}",
                "session-1",
                $"trace-{variant}",
                $"span-{variant}",
                null,
                $"event-{variant}",
                null,
                new(2026, 7, 21, 1, 2, 3, TimeSpan.Zero)));
        var alert = AlertReceiptConsumerV1.Validate(alertBytes);
        var receipt = new SanitizedExportRecord(
            $"alert-receipts/{alert.AlertId}.json", "alert_receipt", alert.AlertId, alert.SessionId,
            alert.TraceId, alert.SourceSurface, null, null, null, alert.LastObservedAt, alertBytes, []);
        var snapshot = new SanitizedExportSourceSnapshot(
            variant is null ? "snapshot-alert-only" : $"snapshot-alert-only-{variant}",
            "local-monitor-test", [new(alert.SourceSurface, "1.2.3")],
            [receipt], new("missing", "available", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(alert.LastObservedAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] RepositoryBundleWithDependency(
        SanitizedExportDependencyDisposition disposition,
        DateTimeOffset createdAt,
        string dependencyRecordType = "alert_receipt",
        string dependencyRecordId = "dependency-record")
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var record = new SanitizedExportRecord(
            "repository-metadata/dependency-source.json", "repository_metadata_projection", "dependency-source",
            "dependency-source", "trace-a", "github-copilot-cli", "repository-a", "sample-workspace", "sample-snapshot",
            observedAt, RepositoryMetadataProjectionV1.Serialize(
                "dependency-source", "dependency-source", "trace-a", "github-copilot-cli", "repository-a",
                "sample-workspace", "sample-snapshot", observedAt, "partial", "not_captured", "retained_by_policy"),
            [new(dependencyRecordType, dependencyRecordId, disposition)], "partial", "not_captured", "retained_by_policy");
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-dependency", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], [record],
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(createdAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static SanitizedImportRecord ImportRecord(string recordType, string recordId, string path, byte[] bytes) => new(
        path, recordType, recordId,
        SanitizedImportIdentity.Hash("sanitized-import-record.v1", recordType, recordId),
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), bytes);

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

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF;" + sql;
        command.ExecuteNonQuery();
    }

    private static void AssertCountInvariant(
        int eligible,
        int added,
        int updated,
        int skipped,
        int rejected,
        int duplicates,
        int conflicts)
    {
        Assert.Equal(eligible, added + updated + skipped + rejected);
        Assert.InRange(duplicates, 0, skipped);
        Assert.InRange(conflicts, 0, rejected);
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

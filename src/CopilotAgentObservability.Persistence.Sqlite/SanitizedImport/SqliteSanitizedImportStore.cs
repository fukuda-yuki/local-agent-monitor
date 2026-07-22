using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.SanitizedImport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;

public sealed class SqliteSanitizedImportStore
{
    private const int BusyTimeoutMilliseconds = 5000;
    private static readonly SanitizedImportMigration Migration = new(
        1,
        SanitizedImportContractVersions.MigrationChain,
        SanitizedImportContractVersions.MigrationStep,
        SanitizedImportContractVersions.MigrationChainSha256,
        Lossy: false);
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? checkpoint;

    public SqliteSanitizedImportStore(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal SqliteSanitizedImportStore(string databasePath, TimeProvider timeProvider, Action<string> checkpoint)
        : this(databasePath, timeProvider)
    {
        this.checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public void CreateSchema() => CreateSchema(validateForeignKeys: true);

    private void CreateSchema(bool validateForeignKeys)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SanitizedImportSchemaV1.Ensure(connection, transaction, validateForeignKeys);
        transaction.Commit();
    }

    public SanitizedImportPreview Preview(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.LongLength > SanitizedExportLimits.MaximumUncompressedBytes)
            return Failure("bundle_too_large");
        return Preview(SanitizedImportArchiveSnapshot.Capture(archiveBytes));
    }

    internal SanitizedImportPreview Preview(SanitizedImportArchiveSnapshot archiveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(archiveSnapshot);
        var read = SanitizedImportBundleReader.Read(archiveSnapshot);
        if (!read.Success) return Failure(read.ErrorCode!);
        try
        {
            EnsureDatabaseDirectory();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            SanitizedImportSchemaV1.Ensure(connection, transaction, validateForeignKeys: false);
            StoredHistory? history;
            try
            {
                history = ReadHistory(connection, transaction, read.Bundle!.ArchiveSha256);
            }
            catch (Exception exception) when (IsStoredDataException(exception))
            {
                throw new SanitizedImportIntegrityException();
            }
            if ((history is not null && !ReplayIntegrityMatches(connection, transaction, read.Bundle, history))
                || (history is null && HasImportFootprint(connection, transaction, read.Bundle.ArchiveSha256)))
                throw new SanitizedImportIntegrityException();
            var evaluation = Evaluate(connection, transaction, read.Bundle!);
            ValidateForeignKeysForImport(connection, transaction);
            transaction.Commit();
            return ToPreview(read.Bundle!, evaluation);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure("import_store_busy");
        }
        catch (SanitizedImportIntegrityException)
        {
            return Failure("import_integrity_failed");
        }
        catch (Exception exception) when (IsStoredDataException(exception))
        {
            return Failure("import_integrity_failed");
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException
            or IOException or UnauthorizedAccessException or OverflowException or FormatException or InvalidCastException)
        {
            return Failure("import_store_unavailable");
        }
    }

    public SanitizedImportResult Commit(byte[] archiveBytes, string previewDigest)
    {
        if (!IsHash(previewDigest)) return ResultFailure("preview_digest_invalid");
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.LongLength > SanitizedExportLimits.MaximumUncompressedBytes)
            return ResultFailure("bundle_too_large");
        return Commit(SanitizedImportArchiveSnapshot.Capture(archiveBytes), previewDigest);
    }

    internal SanitizedImportResult Commit(
        SanitizedImportArchiveSnapshot archiveSnapshot,
        string previewDigest)
    {
        ArgumentNullException.ThrowIfNull(archiveSnapshot);
        if (!IsHash(previewDigest)) return ResultFailure("preview_digest_invalid");
        var preflightError = PreflightError(archiveSnapshot);
        if (preflightError is not null) return ResultFailure(preflightError);
        try
        {
            EnsureDatabaseDirectory();
            checkpoint?.Invoke("after_archive_preflight");
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var read = SanitizedImportBundleReader.Read(archiveSnapshot);
            if (!read.Success) return RollbackWithoutWrite(transaction, ResultFailure(read.ErrorCode!));
            SanitizedImportSchemaV1.Ensure(connection, transaction, validateForeignKeys: false);
            var historyExists = ScalarLong(connection, transaction,
                "SELECT COUNT(*) FROM sanitized_import_history WHERE import_id=$id;",
                ("$id", read.Bundle!.ArchiveSha256)) != 0;
            if (historyExists)
            {
                StoredHistory? existingHistory;
                try
                {
                    existingHistory = ReadHistory(connection, transaction, read.Bundle.ArchiveSha256);
                }
                catch (Exception exception) when (IsStoredDataException(exception))
                {
                    return RollbackWithoutWrite(transaction, ResultFailure("import_integrity_failed"));
                }
                if (existingHistory is null
                    || !ReplayIntegrityMatches(connection, transaction, read.Bundle, existingHistory))
                    return RollbackWithoutWrite(transaction, ResultFailure("import_integrity_failed"));
                ValidateForeignKeysForImport(connection, transaction);
                var replayEvaluation = Evaluate(connection, transaction, read.Bundle);
                if (previewDigest != existingHistory.Item.PreviewDigest && previewDigest != replayEvaluation.PreviewDigest)
                    return RollbackWithoutWrite(transaction, RejectedResult("preview_changed", read.Bundle, replayEvaluation));
                transaction.Commit();
                return ToResult(existingHistory.Item, idempotentReplay: true);
            }
            if (HasImportFootprint(connection, transaction, read.Bundle.ArchiveSha256))
                return RollbackWithoutWrite(transaction, ResultFailure("import_integrity_failed"));
            var evaluation = Evaluate(connection, transaction, read.Bundle);
            ValidateForeignKeysForImport(connection, transaction);
            if (evaluation.Conflicts.Count != 0)
                return RollbackWithoutWrite(transaction, RejectedResult("record_conflict", read.Bundle, evaluation));
            if (previewDigest != evaluation.PreviewDigest)
                return RollbackWithoutWrite(transaction, RejectedResult("preview_changed", read.Bundle, evaluation));

            var importedAt = timeProvider.GetUtcNow().ToUniversalTime();
            InsertHistory(connection, transaction, read.Bundle, evaluation, importedAt);
            foreach (var state in evaluation.RecordStates.Where(item => item.State == "new"))
                InsertRecord(connection, transaction, read.Bundle.ArchiveSha256, state.Record, importedAt);
            checkpoint?.Invoke("after_records");
            foreach (var record in read.Bundle.Records)
                InsertOrigin(connection, transaction, read.Bundle, record, importedAt);
            checkpoint?.Invoke("after_origins");
            foreach (var node in read.Bundle.GraphNodes)
                UpsertNode(connection, transaction, read.Bundle.ArchiveSha256, node);
            foreach (var declaration in read.Bundle.GraphDeclarations)
                InsertDeclaration(connection, transaction, read.Bundle.ArchiveSha256, declaration);
            foreach (var edge in read.Bundle.GraphEdges.Where(item => evaluation.NewRecordIds.Contains(item.SourceRecordLocalId)))
                InsertEdge(connection, transaction, read.Bundle.ArchiveSha256, edge, evaluation.EdgeResolutions[edge.LocalEdgeId]);
            checkpoint?.Invoke("after_graph");
            SanitizedImportSchemaV1.Validate(connection, transaction);
            var history = ReadHistory(connection, transaction, read.Bundle.ArchiveSha256)
                ?? throw new InvalidOperationException();
            var integritySha256 = ComputeIntegritySha256(connection, transaction, read.Bundle, history.Item);
            UpdateHistoryIntegrity(connection, transaction, read.Bundle.ArchiveSha256, integritySha256);
            transaction.Commit();
            return ToResult(history.Item, idempotentReplay: false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return ResultFailure("import_store_busy");
        }
        catch (SanitizedImportIntegrityException)
        {
            return ResultFailure("import_integrity_failed");
        }
        catch (Exception exception) when (IsStoredDataException(exception))
        {
            return ResultFailure("import_integrity_failed");
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException
            or IOException or UnauthorizedAccessException or OverflowException or FormatException or InvalidCastException)
        {
            return ResultFailure("import_transaction_failed");
        }
    }

    private static string? PreflightError(SanitizedImportArchiveSnapshot archiveSnapshot)
    {
        var read = SanitizedImportBundleReader.Read(archiveSnapshot);
        return read.Success ? null : read.ErrorCode;
    }

    private static void ValidateForeignKeysForImport(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        try
        {
            SanitizedImportSchemaV1.ValidateForeignKeys(connection, transaction);
        }
        catch (InvalidOperationException)
        {
            throw new SanitizedImportIntegrityException();
        }
    }

    public SanitizedImportHistoryPage ListHistory(int limit = SanitizedImportLimits.DefaultHistoryItems)
    {
        if (limit is < 1 or > SanitizedImportLimits.MaximumHistoryItems) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        SanitizedImportSchemaV1.Validate(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT import_id,archive_sha256,preview_digest,integrity_sha256,status,
                   eligible_records,new_records,updated_records,skipped_records,rejected_records,duplicate_records,conflict_records,
                   graph_nodes,graph_declarations,graph_state_updates,graph_edges,
                   raw_retention_items,migration_version,migration_chain,migration_step,migration_chain_sha256,
                   source_snapshot_id,source_local_monitor_version,imported_at
            FROM sanitized_import_history
            ORDER BY imported_at DESC,import_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var items = new List<SanitizedImportHistoryItem>();
        while (reader.Read()) items.Add(ReadHistoryRow(reader).Item);
        transaction.Commit();
        return new(SanitizedImportContractVersions.History, items);
    }

    public SanitizedImportHistoryItem? GetHistory(string importId)
    {
        if (!IsHash(importId)) return null;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        SanitizedImportSchemaV1.Validate(connection, transaction);
        var item = ReadHistory(connection, transaction, importId)?.Item;
        transaction.Commit();
        return item;
    }

    private static Evaluation Evaluate(SqliteConnection connection, SqliteTransaction transaction, SanitizedImportBundle bundle)
    {
        if (!StoredImportIntegrityClosureMatches(connection, transaction, ReadAllImportIds(connection, transaction)))
            throw new SanitizedImportIntegrityException();
        var existingRecords = ReadExistingRecords(connection, transaction, bundle.Records);
        var recordStates = new List<RecordState>(bundle.Records.Count);
        var conflicts = new List<SanitizedImportConflict>();
        foreach (var record in bundle.Records)
        {
            if (!existingRecords.TryGetValue((record.RecordType, record.RecordId), out var existing))
            {
                recordStates.Add(new(record, "new", null));
            }
            else if (existing.Sha256 == record.CanonicalSha256 && existing.Bytes.AsSpan().SequenceEqual(record.CanonicalBytes))
            {
                recordStates.Add(new(record, "duplicate", existing.Sha256));
            }
            else
            {
                recordStates.Add(new(record, "conflict", existing.Sha256));
                conflicts.Add(new(record.RecordType, record.RecordId, record.CanonicalSha256, existing.Sha256));
            }
        }

        var historyExists = ScalarLong(connection, transaction,
            "SELECT COUNT(*) FROM sanitized_import_history WHERE import_id=$id;", ("$id", bundle.ArchiveSha256)) != 0;
        var incomingNodes = bundle.GraphNodes.ToDictionary(item => item.LocalNodeId, StringComparer.Ordinal);
        var existingNodes = ReadStoredGraphNodes(connection, transaction, incomingNodes.Keys);
        var definitionsByRecord = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var definingRecordIds = existingNodes.Values
            .Where(item => item.State == "defined")
            .Select(item => item.DefiningRecordLocalId)
            .ToArray();
        if (definingRecordIds.Any(item => item is null)) throw new SanitizedImportIntegrityException();
        var exactDefiningRecordIds = definingRecordIds.Select(item => item!).ToArray();
        var definingRecordOwners = ReadRecordFirstImportIds(connection, transaction, exactDefiningRecordIds);
        if (definingRecordOwners.Count != exactDefiningRecordIds.Distinct(StringComparer.Ordinal).Count())
            throw new SanitizedImportIntegrityException();
        var ownerImportIds = existingRecords.Values.Select(item => item.FirstImportId)
            .Concat(existingNodes.Values.Select(item => item.FirstImportId))
            .Concat(definingRecordOwners.Values)
            .Distinct(StringComparer.Ordinal);
        if (!StoredImportIntegrityClosureMatches(connection, transaction, ownerImportIds))
            throw new SanitizedImportIntegrityException();
        foreach (var existing in existingNodes)
        {
            var incoming = incomingNodes[existing.Key];
            if (existing.Value.NodeKind != incoming.NodeKind || existing.Value.SourceId != incoming.SourceId
                || (existing.Value.State == "defined"
                    && (existing.Value.DefiningRecordLocalId is null
                        || !ReadDefinedNodeIds(connection, transaction, existing.Value.DefiningRecordLocalId, definitionsByRecord)
                            .Contains(existing.Key))))
                throw new SanitizedImportIntegrityException();
        }
        var globalNodeStates = bundle.GraphNodes.ToDictionary(
            item => item.LocalNodeId,
            item => item.State == "defined"
                || existingNodes.TryGetValue(item.LocalNodeId, out var state) && state.State == "defined"
                    ? "defined"
                    : "unresolved",
            StringComparer.Ordinal);
        var edgeResolutions = bundle.GraphEdges.ToDictionary(
            item => item.LocalEdgeId,
            item => globalNodeStates[item.TargetNodeId] == "defined"
                ? "resolved"
                : incomingNodes[item.TargetNodeId].State,
            StringComparer.Ordinal);
        var newRecordIds = recordStates.Where(item => item.State == "new").Select(item => item.Record.LocalRecordId).ToHashSet(StringComparer.Ordinal);
        var newNodes = bundle.GraphNodes.Count(item => !existingNodes.ContainsKey(item.LocalNodeId));
        var graphStateUpdates = bundle.GraphNodes.Count(item => item.State == "defined"
            && existingNodes.TryGetValue(item.LocalNodeId, out var existingState)
            && existingState.State == "unresolved");
        var newEdges = bundle.GraphEdges.Count(item => newRecordIds.Contains(item.SourceRecordLocalId));
        var unresolved = bundle.GraphNodes
            .Where(item => item.State != "defined" && globalNodeStates[item.LocalNodeId] != "defined")
            .Select(item => new SanitizedImportUnresolved(item.NodeKind, item.SourceId, item.State))
            .OrderBy(item => item.NodeKind, StringComparer.Ordinal).ThenBy(item => item.SourceId, StringComparer.Ordinal).ToArray();
        var writesRejected = historyExists || conflicts.Count != 0;
        var duplicateRecords = recordStates.Count(item => item.State == "duplicate");
        var counts = new RecordCounts(
            bundle.Records.Count,
            newRecordIds.Count,
            0,
            duplicateRecords,
            conflicts.Count,
            duplicateRecords,
            conflicts.Count);
        var expected = new SanitizedImportExpectedChanges(
            writesRejected ? 0 : newRecordIds.Count,
            writesRejected ? 0 : bundle.Records.Count,
            writesRejected ? 0 : newNodes,
            writesRejected ? 0 : bundle.GraphDeclarations.Count,
            writesRejected ? 0 : graphStateUpdates,
            writesRejected ? 0 : newEdges,
            writesRejected ? 0 : 1,
            0);
        var digest = PreviewDigest(bundle, recordStates, counts, globalNodeStates, edgeResolutions, expected);
        return new(recordStates, conflicts, unresolved, counts, expected, digest, historyExists,
            globalNodeStates, edgeResolutions, newRecordIds);
    }

    private static SanitizedImportPreview ToPreview(SanitizedImportBundle bundle, Evaluation evaluation) => new(
        true,
        null,
        SanitizedImportContractVersions.Preview,
        bundle.ArchiveSha256,
        evaluation.PreviewDigest,
        bundle.ArchiveSha256,
        bundle.Manifest.ManifestSchemaVersion,
        bundle.Manifest.BundleSchemaVersion,
        bundle.Manifest.BundleProfile,
        SanitizedImportContractVersions.Compatibility,
        Migration,
        bundle.Manifest.SnapshotId,
        bundle.Manifest.SourceLocalMonitorVersion,
        bundle.Manifest.CreatedAt,
        bundle.Manifest.SourceAgentVersions,
        bundle.Manifest.Selection,
        bundle.Manifest.DateRange,
        bundle.Manifest.SourceLabels,
        bundle.Manifest.RecordCounts,
        bundle.Manifest.Capabilities,
        bundle.Manifest.CompletenessDistribution,
        bundle.Manifest.ContentStateDistribution,
        bundle.Manifest.RetentionStateDistribution,
        bundle.Manifest.ProcessingVersions,
        bundle.Records.Count,
        bundle.TotalUncompressedBytes,
        evaluation.Counts.Eligible,
        evaluation.Counts.New,
        evaluation.Counts.Updated,
        evaluation.Counts.Skipped,
        evaluation.Counts.Rejected,
        evaluation.Counts.Duplicate,
        evaluation.Counts.Conflict,
        evaluation.ExpectedChanges.GraphStateUpdates,
        evaluation.Conflicts.Take(SanitizedImportLimits.MaximumPreviewConflicts).ToArray(),
        bundle.Manifest.KnownMissingEvidence.Count,
        bundle.Manifest.KnownMissingEvidence.Take(SanitizedImportLimits.MaximumPreviewManifestDeclarations).ToArray(),
        evaluation.Unresolved.Length,
        evaluation.Unresolved.Take(SanitizedImportLimits.MaximumPreviewUnresolved).ToArray(),
        evaluation.ExpectedChanges,
        evaluation.Conflicts.Count == 0);

    internal static SanitizedImportPreview Failure(string code) => new(
        false, code, SanitizedImportContractVersions.Preview, null, null, null, null, null, null,
        "unsupported", Migration, null, null, null, [], null, null, [],
        new Dictionary<string, int>(), null, new Dictionary<string, int>(), new Dictionary<string, int>(),
        new Dictionary<string, int>(), new Dictionary<string, string>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, [], 0, [],
        new(0, 0, 0, 0, 0, 0, 0, 0), false);

    internal static SanitizedImportResult ResultFailure(string code) => new(
        false, code, SanitizedImportContractVersions.Result, null, null, null, null,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Migration, null, null, null, false);

    private static SanitizedImportResult RejectedResult(
        string code,
        SanitizedImportBundle bundle,
        Evaluation evaluation)
    {
        var counts = evaluation.Counts;
        return new(
            false, code, SanitizedImportContractVersions.Result, null, bundle.ArchiveSha256,
            evaluation.PreviewDigest, null, counts.Eligible, counts.New, counts.Updated, counts.Skipped,
            counts.Rejected, counts.Duplicate, counts.Conflict, 0, 0, 0, 0, 0, Migration,
            bundle.Manifest.SnapshotId, bundle.Manifest.SourceLocalMonitorVersion, null, false);
    }

    private static SanitizedImportResult RollbackWithoutWrite(SqliteTransaction transaction, SanitizedImportResult result)
    {
        transaction.Rollback();
        return result;
    }

    private static SanitizedImportResult ToResult(SanitizedImportHistoryItem item, bool idempotentReplay) => new(
        true, null, SanitizedImportContractVersions.Result, item.ImportId, item.ArchiveSha256, item.PreviewDigest,
        item.Status, item.EligibleRecords, item.NewRecords, item.UpdatedRecords, item.SkippedRecords,
        item.RejectedRecords, item.DuplicateRecords, item.ConflictRecords, item.GraphNodes,
        item.GraphDeclarations, item.GraphStateUpdates, item.GraphEdges,
        item.RawRetentionItems, item.Migration, item.SourceSnapshotId, item.SourceLocalMonitorVersion,
        item.ImportedAt, idempotentReplay);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON;");
        Execute(connection, null, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
        return connection;
    }

    private void EnsureDatabaseDirectory()
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    private static Dictionary<(string Type, string Id), ExistingRecord> ReadExistingRecords(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SanitizedImportRecord> records)
    {
        var result = new Dictionary<(string, string), ExistingRecord>();
        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT canonical_sha256,canonical_json,first_import_id FROM sanitized_import_records WHERE record_type=$type AND source_record_id=$id;";
            command.Parameters.AddWithValue("$type", record.RecordType);
            command.Parameters.AddWithValue("$id", record.RecordId);
            using var reader = command.ExecuteReader();
            if (reader.Read()) result[(record.RecordType, record.RecordId)] = new(
                reader.GetString(0), (byte[])reader[1], reader.GetString(2));
        }
        return result;
    }

    private static StoredHistory? ReadHistory(SqliteConnection connection, SqliteTransaction transaction, string importId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT import_id,archive_sha256,preview_digest,integrity_sha256,status,
                   eligible_records,new_records,updated_records,skipped_records,rejected_records,duplicate_records,conflict_records,
                   graph_nodes,graph_declarations,graph_state_updates,graph_edges,
                   raw_retention_items,migration_version,migration_chain,migration_step,migration_chain_sha256,
                   source_snapshot_id,source_local_monitor_version,imported_at
            FROM sanitized_import_history WHERE import_id=$id;
            """;
        command.Parameters.AddWithValue("$id", importId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHistoryRow(reader) : null;
    }

    private static bool HasImportFootprint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importId) => ScalarLong(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM sanitized_import_origins WHERE import_id=$id)
                OR EXISTS(SELECT 1 FROM sanitized_import_records WHERE first_import_id=$id)
                OR EXISTS(SELECT 1 FROM sanitized_import_graph_declarations WHERE import_id=$id)
                OR EXISTS(SELECT 1 FROM sanitized_import_graph_nodes WHERE first_import_id=$id)
                OR EXISTS(SELECT 1 FROM sanitized_import_graph_edges WHERE first_import_id=$id);
            """, ("$id", importId)) != 0;

    private static StoredHistory ReadHistoryRow(SqliteDataReader reader) => new(
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8),
            reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12),
            reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
            new(reader.GetInt32(17), reader.GetString(18), reader.GetString(19), reader.GetString(20), false),
            reader.GetString(21), reader.GetString(22), ParseTimestamp(reader.GetString(23))),
        reader.GetString(3));

    private static void InsertHistory(SqliteConnection connection, SqliteTransaction transaction, SanitizedImportBundle bundle, Evaluation evaluation, DateTimeOffset importedAt) =>
        Execute(connection, transaction, """
            INSERT INTO sanitized_import_history(
                import_id,archive_sha256,preview_digest,integrity_sha256,status,
                eligible_records,new_records,updated_records,skipped_records,rejected_records,duplicate_records,conflict_records,
                graph_nodes,graph_declarations,graph_state_updates,graph_edges,
                raw_retention_items,migration_version,migration_chain,migration_step,migration_chain_sha256,
                source_snapshot_id,source_local_monitor_version,imported_at)
            VALUES($id,$archive,$preview,$integrity,'committed',
                   $eligible,$new,$updated,$skipped,$rejected,$duplicate,$conflict,
                   $nodes,$declarations,$state_updates,$edges,0,1,$chain,$step,$chain_hash,$snapshot,$monitor,$at);
            """,
            ("$id", bundle.ArchiveSha256), ("$archive", bundle.ArchiveSha256), ("$preview", evaluation.PreviewDigest),
            ("$integrity", new string('0', 64)),
            ("$eligible", evaluation.Counts.Eligible), ("$new", evaluation.Counts.New),
            ("$updated", evaluation.Counts.Updated), ("$skipped", evaluation.Counts.Skipped),
            ("$rejected", evaluation.Counts.Rejected), ("$duplicate", evaluation.Counts.Duplicate),
            ("$conflict", evaluation.Counts.Conflict),
            ("$nodes", evaluation.ExpectedChanges.GraphNodes),
            ("$declarations", evaluation.ExpectedChanges.GraphDeclarations),
            ("$state_updates", evaluation.ExpectedChanges.GraphStateUpdates),
            ("$edges", evaluation.ExpectedChanges.GraphEdges),
            ("$chain", SanitizedImportContractVersions.MigrationChain), ("$step", SanitizedImportContractVersions.MigrationStep),
            ("$chain_hash", SanitizedImportContractVersions.MigrationChainSha256), ("$snapshot", bundle.Manifest.SnapshotId),
            ("$monitor", bundle.Manifest.SourceLocalMonitorVersion), ("$at", Timestamp(importedAt)));

    private static void InsertRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importId,
        SanitizedImportRecord record,
        DateTimeOffset importedAt) =>
        Execute(connection, transaction, "INSERT INTO sanitized_import_records(local_record_id,record_type,source_record_id,canonical_sha256,canonical_json,first_import_id,created_at) VALUES($local,$type,$source,$hash,$json,$import,$at);",
            ("$local", record.LocalRecordId), ("$type", record.RecordType), ("$source", record.RecordId),
            ("$hash", record.CanonicalSha256), ("$json", record.CanonicalBytes), ("$import", importId),
            ("$at", Timestamp(importedAt)));

    private static void InsertOrigin(SqliteConnection connection, SqliteTransaction transaction, SanitizedImportBundle bundle, SanitizedImportRecord record, DateTimeOffset importedAt) =>
        Execute(connection, transaction, "INSERT INTO sanitized_import_origins(import_id,local_record_id,entry_path,source_snapshot_id,source_local_monitor_version,source_created_at,imported_at) VALUES($import,$record,$path,$snapshot,$monitor,$created,$at);",
            ("$import", bundle.ArchiveSha256), ("$record", record.LocalRecordId), ("$path", record.EntryPath),
            ("$snapshot", bundle.Manifest.SnapshotId), ("$monitor", bundle.Manifest.SourceLocalMonitorVersion),
            ("$created", Timestamp(bundle.Manifest.CreatedAt)), ("$at", Timestamp(importedAt)));

    private static void InsertDeclaration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importId,
        SanitizedImportGraphDeclaration declaration) =>
        Execute(connection, transaction,
            "INSERT INTO sanitized_import_graph_declarations(import_id,local_node_id,declared_state) VALUES($import,$node,$state);",
            ("$import", importId), ("$node", declaration.LocalNodeId), ("$state", declaration.DeclaredState));

    private static void UpsertNode(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importId,
        SanitizedImportGraphNode node)
    {
        var incomingState = node.State == "defined" ? "defined" : "unresolved";
        Execute(connection, transaction, """
            INSERT INTO sanitized_import_graph_nodes(local_node_id,node_kind,source_id,state,defining_record_local_id,first_import_id)
            VALUES($id,$kind,$source,$incoming,$record,$import)
            ON CONFLICT(local_node_id) DO UPDATE SET
                state=CASE
                    WHEN sanitized_import_graph_nodes.state='defined' OR excluded.state='defined' THEN 'defined'
                    ELSE 'unresolved'
                END,
                defining_record_local_id=CASE
                    WHEN sanitized_import_graph_nodes.state='defined' THEN sanitized_import_graph_nodes.defining_record_local_id
                    WHEN excluded.state='defined' THEN excluded.defining_record_local_id
                    ELSE NULL
                END;
            """,
            ("$id", node.LocalNodeId), ("$kind", node.NodeKind), ("$source", node.SourceId),
            ("$incoming", incomingState), ("$record", node.DefiningRecordLocalId), ("$import", importId));
    }

    private static void InsertEdge(SqliteConnection connection, SqliteTransaction transaction, string importId, SanitizedImportGraphEdge edge, string resolution) =>
        Execute(connection, transaction, "INSERT INTO sanitized_import_graph_edges(local_edge_id,source_record_local_id,source_node_id,target_node_id,relation,edge_ordinal,resolution_state,provenance_json,first_import_id) VALUES($id,$record,$source,$target,$relation,$ordinal,$resolution,$provenance,$import);",
            ("$id", edge.LocalEdgeId), ("$record", edge.SourceRecordLocalId), ("$source", edge.SourceNodeId),
            ("$target", edge.TargetNodeId), ("$relation", edge.Relation), ("$ordinal", edge.Ordinal),
            ("$resolution", resolution), ("$provenance", edge.ProvenanceJson), ("$import", importId));

    private static void UpdateHistoryIntegrity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importId,
        string integritySha256) =>
        Execute(connection, transaction,
            "UPDATE sanitized_import_history SET integrity_sha256=$integrity WHERE import_id=$import;",
            ("$integrity", integritySha256), ("$import", importId));

    private static bool ReplayIntegrityMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SanitizedImportBundle bundle,
        StoredHistory history)
    {
        try
        {
            var item = history.Item;
            var scope = new ImportIntegrityScope(
                bundle.ArchiveSha256,
                bundle.Records,
                bundle.GraphNodes,
                bundle.GraphEdges,
                bundle.GraphDeclarations);
            if (!HistoryHeaderMatches(history, scope)
                || item.SourceSnapshotId != bundle.Manifest.SnapshotId
                || item.SourceLocalMonitorVersion != bundle.Manifest.SourceLocalMonitorVersion
                || !HistoryCountsMatch(connection, transaction, scope, item)
                || !OwnerReceiptClosureIsConsistent(connection, transaction, scope))
                return false;

            var actual = ComputeIntegritySha256(connection, transaction, scope, item);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(history.IntegritySha256),
                Convert.FromHexString(actual))
                && GraphStateIsConsistent(connection, transaction, scope);
        }
        catch (Exception exception) when (IsStoredDataException(exception))
        {
            return false;
        }
    }

    private static bool StoredImportIntegrityClosureMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> importIds)
    {
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var importId in importIds.Distinct(StringComparer.Ordinal)) queue.Enqueue(importId);
        while (queue.TryDequeue(out var importId))
        {
            if (!visited.Add(importId)) continue;
            if (!StoredImportIntegrityMatches(connection, transaction, importId, out var dependencies)) return false;
            foreach (var dependency in dependencies)
                if (!visited.Contains(dependency)) queue.Enqueue(dependency);
        }
        return true;
    }

    private static bool StoredImportIntegrityMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importId,
        out IReadOnlyList<string> dependencies)
    {
        dependencies = [];
        try
        {
            var history = ReadHistory(connection, transaction, importId);
            if (history is null || ReadStoredImportScope(connection, transaction, history.Item) is not { } scope
                || !HistoryHeaderMatches(history, scope)
                || !HistoryCountsMatch(connection, transaction, scope, history.Item)
                || !GraphStateIsConsistent(connection, transaction, scope)
                || !TryReadOwnerImportIds(connection, transaction, scope, out dependencies))
                return false;
            var actual = ComputeIntegritySha256(connection, transaction, scope, history.Item);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(history.IntegritySha256),
                Convert.FromHexString(actual));
        }
        catch (Exception exception) when (IsStoredDataException(exception))
        {
            dependencies = [];
            return false;
        }
    }

    private static bool OwnerReceiptClosureIsConsistent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportIntegrityScope scope) =>
        TryReadOwnerImportIds(connection, transaction, scope, out var owners)
        && StoredImportIntegrityClosureMatches(connection, transaction, owners);

    private static bool TryReadOwnerImportIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportIntegrityScope scope,
        out IReadOnlyList<string> importIds)
    {
        importIds = [];
        var recordIds = scope.Records.Select(item => item.LocalRecordId).Distinct(StringComparer.Ordinal).ToArray();
        var recordOwners = ReadRecordFirstImportIds(connection, transaction, recordIds);
        if (recordOwners.Count != recordIds.Length) return false;

        var nodeIds = scope.Nodes.Select(item => item.LocalNodeId).Distinct(StringComparer.Ordinal).ToArray();
        var nodes = ReadStoredGraphNodes(connection, transaction, nodeIds);
        if (nodes.Count != nodeIds.Length) return false;
        if (nodes.Values.Any(item => item.State is not ("defined" or "unresolved")
                || (item.State == "defined") != (item.DefiningRecordLocalId is not null)))
            return false;
        var definingRecordIds = nodes.Values
            .Where(item => item.State == "defined")
            .Select(item => item.DefiningRecordLocalId)
            .ToArray();
        if (definingRecordIds.Any(item => item is null)) return false;
        var exactDefiningRecordIds = definingRecordIds.Select(item => item!).Distinct(StringComparer.Ordinal).ToArray();
        var definingRecordOwners = ReadRecordFirstImportIds(connection, transaction, exactDefiningRecordIds);
        if (definingRecordOwners.Count != exactDefiningRecordIds.Length) return false;

        var owners = recordOwners.Values
            .Concat(nodes.Values.Select(item => item.FirstImportId))
            .Concat(definingRecordOwners.Values)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (owners.Any(importId => !IsHash(importId))) return false;
        importIds = owners;
        return true;
    }

    private static IReadOnlyList<string> ReadAllImportIds(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT import_id FROM sanitized_import_history ORDER BY import_id;";
        using var reader = command.ExecuteReader();
        var importIds = new List<string>();
        while (reader.Read()) importIds.Add(reader.GetString(0));
        return importIds;
    }

    private static bool HistoryHeaderMatches(StoredHistory history, ImportIntegrityScope scope)
    {
        var item = history.Item;
        return IsHash(history.IntegritySha256)
            && item.ImportId == scope.ImportId
            && item.ArchiveSha256 == scope.ImportId
            && item.Status == "committed"
            && item.EligibleRecords == scope.Records.Count
            && item.NewRecords + item.UpdatedRecords + item.SkippedRecords + item.RejectedRecords == item.EligibleRecords
            && item.DuplicateRecords <= item.SkippedRecords
            && item.ConflictRecords <= item.RejectedRecords
            && item.UpdatedRecords == 0
            && item.RejectedRecords == 0
            && item.ConflictRecords == 0
            && item.Migration == Migration;
    }

    private static bool GraphStateIsConsistent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportIntegrityScope scope)
    {
        var definitionsByRecord = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var storedNodes = ReadStoredGraphNodes(connection, transaction, scope.Nodes.Select(item => item.LocalNodeId));
        var definingRecordOwners = ReadRecordFirstImportIds(connection, transaction,
            storedNodes.Values.Where(item => item.DefiningRecordLocalId is not null)
                .Select(item => item.DefiningRecordLocalId!));
        foreach (var node in scope.Nodes)
        {
            if (!storedNodes.TryGetValue(node.LocalNodeId, out var stored)) return false;
            var state = stored.State;
            var definingRecord = stored.DefiningRecordLocalId;
            var firstImport = stored.FirstImportId;
            if (stored.NodeKind != node.NodeKind || stored.SourceId != node.SourceId) return false;
            if (node.State == "defined")
            {
                if (state != "defined" || definingRecord is null) return false;
                if (firstImport == scope.ImportId && definingRecord != node.DefiningRecordLocalId) return false;
                if (definingRecordOwners.TryGetValue(definingRecord, out var definingImport)
                    && definingImport == scope.ImportId
                    && definingRecord != node.DefiningRecordLocalId)
                    return false;
            }
            else if (state != "unresolved" && state != "defined")
            {
                return false;
            }
            if (state == "unresolved" && definingRecord is not null) return false;
            if (state == "defined"
                && (definingRecord is null || !ReadDefinedNodeIds(connection, transaction, definingRecord, definitionsByRecord)
                    .Contains(node.LocalNodeId)))
                return false;
        }
        return true;
    }

    private static bool HistoryCountsMatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportIntegrityScope scope,
        SanitizedImportHistoryItem history)
    {
        var bundleRecordIds = scope.Records.Select(item => item.LocalRecordId).ToHashSet(StringComparer.Ordinal);
        var newRecordIds = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT local_record_id FROM sanitized_import_records WHERE first_import_id=$import;";
            command.Parameters.AddWithValue("$import", scope.ImportId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) newRecordIds.Add(reader.GetString(0));
        }

        return newRecordIds.IsSubsetOf(bundleRecordIds)
            && history.NewRecords == newRecordIds.Count
            && history.SkippedRecords == history.EligibleRecords - history.NewRecords
            && history.DuplicateRecords == history.SkippedRecords
            && history.GraphNodes == ScalarLong(connection, transaction,
                "SELECT COUNT(*) FROM sanitized_import_graph_nodes WHERE first_import_id=$import;",
                ("$import", scope.ImportId))
            && history.GraphDeclarations == scope.Declarations.Count
            && history.GraphDeclarations == ScalarLong(connection, transaction,
                "SELECT COUNT(*) FROM sanitized_import_graph_declarations WHERE import_id=$import;",
                ("$import", scope.ImportId))
            && DeclarationsMatch(connection, transaction, scope)
            && history.GraphStateUpdates == CountGraphStateUpdates(connection, transaction, scope)
            && history.GraphEdges == scope.Edges.Count(item => newRecordIds.Contains(item.SourceRecordLocalId))
            && history.GraphEdges == ScalarLong(connection, transaction,
                "SELECT COUNT(*) FROM sanitized_import_graph_edges WHERE first_import_id=$import;",
                ("$import", scope.ImportId))
            && history.EligibleRecords == ScalarLong(connection, transaction,
                "SELECT COUNT(*) FROM sanitized_import_origins WHERE import_id=$import;",
                ("$import", scope.ImportId));
    }

    private static bool DeclarationsMatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportIntegrityScope scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT local_node_id,declared_state FROM sanitized_import_graph_declarations WHERE import_id=$import ORDER BY local_node_id;";
        command.Parameters.AddWithValue("$import", scope.ImportId);
        using var reader = command.ExecuteReader();
        var actual = new List<SanitizedImportGraphDeclaration>();
        while (reader.Read()) actual.Add(new(reader.GetString(0), reader.GetString(1)));
        return actual.SequenceEqual(scope.Declarations.OrderBy(item => item.LocalNodeId, StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, StoredGraphNode> ReadStoredGraphNodes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> nodeIds)
    {
        var result = new Dictionary<string, StoredGraphNode>(StringComparer.Ordinal);
        foreach (var batch in nodeIds.Chunk(400))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = batch.Select((_, index) => $"$id{index}").ToArray();
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            command.CommandText = $"SELECT local_node_id,node_kind,source_id,state,defining_record_local_id,first_import_id FROM sanitized_import_graph_nodes WHERE local_node_id IN ({string.Join(',', names)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = new(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadRecordFirstImportIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> recordIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in recordIds.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = batch.Select((_, index) => $"$id{index}").ToArray();
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            command.CommandText = $"SELECT local_record_id,first_import_id FROM sanitized_import_records WHERE local_record_id IN ({string.Join(',', names)});";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static int CountGraphStateUpdates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportIntegrityScope scope)
    {
        var storedNodes = ReadStoredGraphNodes(connection, transaction,
            scope.Nodes.Where(item => item.State == "defined").Select(item => item.LocalNodeId));
        var definingRecordOwners = ReadRecordFirstImportIds(connection, transaction,
            storedNodes.Values.Where(item => item.DefiningRecordLocalId is not null)
                .Select(item => item.DefiningRecordLocalId!));
        return storedNodes.Values.Count(node => node.FirstImportId != scope.ImportId
            && node.DefiningRecordLocalId is { } recordId
            && definingRecordOwners.TryGetValue(recordId, out var definingImport)
            && definingImport == scope.ImportId);
    }

    private static IReadOnlySet<string> ReadDefinedNodeIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string localRecordId,
        Dictionary<string, IReadOnlySet<string>> cache)
    {
        if (cache.TryGetValue(localRecordId, out var cached)) return cached;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record_type,source_record_id,canonical_sha256,canonical_json FROM sanitized_import_records WHERE local_record_id=$id;";
        command.Parameters.AddWithValue("$id", localRecordId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return cache[localRecordId] = new HashSet<string>(StringComparer.Ordinal);
        var recordType = reader.GetString(0);
        var sourceRecordId = reader.GetString(1);
        var canonicalSha256 = reader.GetString(2);
        var canonicalBytes = (byte[])reader[3];
        if (localRecordId != SanitizedImportIdentity.Hash("sanitized-import-record.v1", recordType, sourceRecordId)
            || canonicalSha256 != Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant())
            return cache[localRecordId] = new HashSet<string>(StringComparer.Ordinal);
        var record = new SanitizedImportRecord(
            string.Empty,
            recordType,
            sourceRecordId,
            localRecordId,
            canonicalSha256,
            canonicalBytes);
        try
        {
            var graph = SanitizedImportGraphProjector.Project([record], []);
            return cache[localRecordId] = graph.Nodes
                .Where(item => item.State == "defined")
                .Select(item => item.LocalNodeId)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException
            or FormatException or OverflowException or AlertReceiptConsumerException
            or InstructionFindingHandoffConsumerValidationException or SanitizedImportGraphLimitException)
        {
            return cache[localRecordId] = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static ImportIntegrityScope? ReadStoredImportScope(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SanitizedImportHistoryItem history)
    {
        var records = new List<SanitizedImportRecord>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT o.entry_path,r.record_type,r.source_record_id,r.local_record_id,
                       r.canonical_sha256,r.canonical_json,o.source_snapshot_id,
                       o.source_local_monitor_version,o.imported_at
                FROM sanitized_import_origins o
                JOIN sanitized_import_records r ON r.local_record_id=o.local_record_id
                WHERE o.import_id=$import
                ORDER BY o.entry_path;
                """;
            command.Parameters.AddWithValue("$import", history.ImportId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var recordType = reader.GetString(1);
                var recordId = reader.GetString(2);
                var localRecordId = reader.GetString(3);
                var canonicalSha256 = reader.GetString(4);
                var canonicalBytes = (byte[])reader[5];
                if (localRecordId != SanitizedImportIdentity.Hash("sanitized-import-record.v1", recordType, recordId)
                    || canonicalSha256 != Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant()
                    || reader.GetString(6) != history.SourceSnapshotId
                    || reader.GetString(7) != history.SourceLocalMonitorVersion
                    || reader.GetString(8) != Timestamp(history.ImportedAt))
                    return null;
                records.Add(new(
                    reader.GetString(0), recordType, recordId, localRecordId, canonicalSha256, canonicalBytes));
            }
        }

        var declarations = new List<SanitizedImportUnresolved>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT n.node_kind,n.source_id,d.declared_state
                FROM sanitized_import_graph_declarations d
                JOIN sanitized_import_graph_nodes n ON n.local_node_id=d.local_node_id
                WHERE d.import_id=$import
                ORDER BY d.local_node_id;
                """;
            command.Parameters.AddWithValue("$import", history.ImportId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) declarations.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        try
        {
            var graph = SanitizedImportGraphProjector.Project(records, declarations);
            var graphDeclarations = declarations
                .Select(item => new SanitizedImportGraphDeclaration(
                    SanitizedImportIdentity.Hash("sanitized-import-node.v1", item.NodeKind, item.SourceId),
                    item.State))
                .OrderBy(item => item.LocalNodeId, StringComparer.Ordinal)
                .ToArray();
            return new(history.ImportId, records, graph.Nodes, graph.Edges, graphDeclarations);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException
            or FormatException or OverflowException or AlertReceiptConsumerException
            or InstructionFindingHandoffConsumerValidationException or SanitizedImportGraphLimitException)
        {
            return null;
        }
    }

    private static string ComputeIntegritySha256(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SanitizedImportBundle bundle,
        SanitizedImportHistoryItem history) => ComputeIntegritySha256(
            connection,
            transaction,
            new ImportIntegrityScope(
                bundle.ArchiveSha256,
                bundle.Records,
                bundle.GraphNodes,
                bundle.GraphEdges,
                bundle.GraphDeclarations),
            history);

    private static string ComputeIntegritySha256(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportIntegrityScope scope,
        SanitizedImportHistoryItem history)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "sanitized-import-owned-integrity.v1");
        Append(hash, scope.ImportId);
        foreach (var value in new[]
                 {
                     history.ImportId, history.ArchiveSha256, history.PreviewDigest, history.Status,
                     history.EligibleRecords.ToString(CultureInfo.InvariantCulture),
                     history.NewRecords.ToString(CultureInfo.InvariantCulture),
                     history.UpdatedRecords.ToString(CultureInfo.InvariantCulture),
                     history.SkippedRecords.ToString(CultureInfo.InvariantCulture),
                     history.RejectedRecords.ToString(CultureInfo.InvariantCulture),
                     history.DuplicateRecords.ToString(CultureInfo.InvariantCulture),
                     history.ConflictRecords.ToString(CultureInfo.InvariantCulture),
                     history.GraphNodes.ToString(CultureInfo.InvariantCulture),
                     history.GraphDeclarations.ToString(CultureInfo.InvariantCulture),
                     history.GraphStateUpdates.ToString(CultureInfo.InvariantCulture),
                     history.GraphEdges.ToString(CultureInfo.InvariantCulture),
                     history.RawRetentionItems.ToString(CultureInfo.InvariantCulture),
                     history.Migration.Version.ToString(CultureInfo.InvariantCulture), history.Migration.Chain,
                     history.Migration.Step, history.Migration.ChainSha256,
                     history.SourceSnapshotId, history.SourceLocalMonitorVersion, Timestamp(history.ImportedAt),
                 })
            Append(hash, value);

        AppendQuery(hash, connection, transaction, "origins", """
            SELECT local_record_id,entry_path,source_snapshot_id,source_local_monitor_version,source_created_at,imported_at
            FROM sanitized_import_origins WHERE import_id=$import ORDER BY local_record_id;
            """, ("$import", scope.ImportId));
        AppendQuery(hash, connection, transaction, "declarations", """
            SELECT local_node_id,declared_state
            FROM sanitized_import_graph_declarations WHERE import_id=$import ORDER BY local_node_id;
            """, ("$import", scope.ImportId));

        AppendIdBatches(hash, connection, transaction, "records",
            scope.Records.Select(item => item.LocalRecordId),
            "SELECT local_record_id,record_type,source_record_id,canonical_sha256,canonical_json,first_import_id,created_at FROM sanitized_import_records WHERE local_record_id IN ({0}) ORDER BY local_record_id;");
        AppendIdBatches(hash, connection, transaction, "nodes",
            scope.Nodes.Select(item => item.LocalNodeId),
            "SELECT local_node_id,node_kind,source_id,first_import_id FROM sanitized_import_graph_nodes WHERE local_node_id IN ({0}) ORDER BY local_node_id;");
        AppendQuery(hash, connection, transaction, "first-import-nodes", """
            SELECT local_node_id,node_kind,source_id,first_import_id
            FROM sanitized_import_graph_nodes WHERE first_import_id=$import ORDER BY local_node_id;
            """, ("$import", scope.ImportId));
        AppendIdBatches(hash, connection, transaction, "source-record-edges",
            scope.Records.Select(item => item.LocalRecordId),
            "SELECT local_edge_id,source_record_local_id,source_node_id,target_node_id,relation,edge_ordinal,resolution_state,provenance_json,first_import_id FROM sanitized_import_graph_edges WHERE source_record_local_id IN ({0}) ORDER BY local_edge_id;");
        AppendQuery(hash, connection, transaction, "first-import-edges", """
            SELECT local_edge_id,source_record_local_id,source_node_id,target_node_id,relation,edge_ordinal,resolution_state,provenance_json,first_import_id
            FROM sanitized_import_graph_edges WHERE first_import_id=$import ORDER BY local_edge_id;
            """, ("$import", scope.ImportId));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendIdBatches(
        IncrementalHash hash,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string label,
        IEnumerable<string> ids,
        string sqlFormat)
    {
        var ordered = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var batchIndex = 0;
        foreach (var batch in ordered.Chunk(400))
        {
            var parameters = batch.Select((value, index) => ($"$id{index}", (object)value)).ToArray();
            Append(hash, label);
            Append(hash, batchIndex++.ToString(CultureInfo.InvariantCulture));
            foreach (var id in batch) Append(hash, id);
            AppendQuery(hash, connection, transaction, "rows",
                string.Format(CultureInfo.InvariantCulture, sqlFormat, string.Join(',', parameters.Select(item => item.Item1))),
                parameters);
        }
        Append(hash, $"{label}-batch-count");
        Append(hash, batchIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendQuery(
        IncrementalHash hash,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string label,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        Append(hash, label);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            Append(hash, "row");
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (reader.IsDBNull(index))
                {
                    Append(hash, "null");
                    continue;
                }
                if (reader.GetValue(index) is byte[] bytes)
                {
                    Append(hash, "bytes");
                    Append(hash, bytes);
                    continue;
                }
                Append(hash, reader.GetFieldType(index) == typeof(string) ? "string" : "number");
                Append(hash, Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)!);
            }
        }
        Append(hash, "row-count");
        Append(hash, rows.ToString(CultureInfo.InvariantCulture));
    }

    private static string PreviewDigest(
        SanitizedImportBundle bundle,
        IReadOnlyList<RecordState> records,
        RecordCounts counts,
        IReadOnlyDictionary<string, string> globalNodeStates,
        IReadOnlyDictionary<string, string> edgeResolutions,
        SanitizedImportExpectedChanges expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "sanitized-import-preview-digest.v1");
        Append(hash, bundle.ArchiveSha256);
        Append(hash, SanitizedImportContractVersions.MigrationChainSha256);
        foreach (var item in records.OrderBy(item => item.Record.RecordType, StringComparer.Ordinal).ThenBy(item => item.Record.RecordId, StringComparer.Ordinal))
        {
            Append(hash, item.Record.RecordType); Append(hash, item.Record.RecordId); Append(hash, item.Record.CanonicalSha256);
            Append(hash, item.State); Append(hash, item.ExistingSha256 ?? string.Empty);
        }
        foreach (var value in new[] { counts.Eligible, counts.New, counts.Updated, counts.Skipped, counts.Rejected, counts.Duplicate, counts.Conflict })
            Append(hash, value.ToString(CultureInfo.InvariantCulture));
        foreach (var item in globalNodeStates.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Append(hash, item.Key);
            Append(hash, item.Value);
        }
        foreach (var declaration in bundle.GraphDeclarations.OrderBy(item => item.LocalNodeId, StringComparer.Ordinal))
        {
            Append(hash, declaration.LocalNodeId);
            Append(hash, declaration.DeclaredState);
        }
        foreach (var edge in bundle.GraphEdges.OrderBy(item => item.LocalEdgeId, StringComparer.Ordinal))
        {
            Append(hash, edge.LocalEdgeId);
            Append(hash, edgeResolutions[edge.LocalEdgeId]);
        }
        foreach (var value in new[]
                 {
                     expected.Records, expected.Origins, expected.GraphNodes, expected.GraphDeclarations,
                     expected.GraphStateUpdates, expected.GraphEdges, expected.HistoryRows, expected.RawRetentionItems,
                 })
            Append(hash, value.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes);
    }

    private static void Append(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }

    private static long ScalarLong(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsStoredDataException(Exception exception) => exception is InvalidCastException
        or FormatException or OverflowException or JsonException or InvalidDataException;
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value,
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed record ExistingRecord(string Sha256, byte[] Bytes, string FirstImportId);
    private sealed record RecordState(SanitizedImportRecord Record, string State, string? ExistingSha256);
    private sealed record RecordCounts(
        int Eligible,
        int New,
        int Updated,
        int Skipped,
        int Rejected,
        int Duplicate,
        int Conflict);
    private sealed record StoredHistory(SanitizedImportHistoryItem Item, string IntegritySha256);
    private sealed record StoredGraphNode(
        string NodeKind,
        string SourceId,
        string State,
        string? DefiningRecordLocalId,
        string FirstImportId);
    private sealed record ImportIntegrityScope(
        string ImportId,
        IReadOnlyList<SanitizedImportRecord> Records,
        IReadOnlyList<SanitizedImportGraphNode> Nodes,
        IReadOnlyList<SanitizedImportGraphEdge> Edges,
        IReadOnlyList<SanitizedImportGraphDeclaration> Declarations);
    private sealed record Evaluation(
        IReadOnlyList<RecordState> RecordStates,
        IReadOnlyList<SanitizedImportConflict> Conflicts,
        SanitizedImportUnresolved[] Unresolved,
        RecordCounts Counts,
        SanitizedImportExpectedChanges ExpectedChanges,
        string PreviewDigest,
        bool HistoryExists,
        IReadOnlyDictionary<string, string> GlobalNodeStates,
        IReadOnlyDictionary<string, string> EdgeResolutions,
        IReadOnlySet<string> NewRecordIds);
    private sealed class SanitizedImportIntegrityException : Exception;
}

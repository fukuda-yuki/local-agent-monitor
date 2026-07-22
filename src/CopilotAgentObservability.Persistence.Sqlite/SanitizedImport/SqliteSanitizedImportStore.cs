using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    public void CreateSchema()
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SanitizedImportSchemaV1.Ensure(connection, transaction);
        transaction.Commit();
    }

    public SanitizedImportPreview Preview(byte[] archiveBytes)
    {
        var read = SanitizedImportBundleReader.Read(archiveBytes);
        if (!read.Success) return Failure(read.ErrorCode!);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            SanitizedImportSchemaV1.Validate(connection, transaction);
            var evaluation = Evaluate(connection, transaction, read.Bundle!);
            transaction.Commit();
            return ToPreview(read.Bundle!, evaluation);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure("import_store_busy");
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException
            or IOException or UnauthorizedAccessException or OverflowException)
        {
            return Failure("import_store_unavailable");
        }
    }

    public SanitizedImportResult Commit(byte[] archiveBytes, string previewDigest)
    {
        if (!IsHash(previewDigest)) return ResultFailure("preview_digest_invalid");
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            SanitizedImportSchemaV1.Validate(connection, transaction);
            var read = SanitizedImportBundleReader.Read(archiveBytes);
            if (!read.Success) return CompleteWithoutWrite(transaction, ResultFailure(read.ErrorCode!));
            var existingHistory = ReadHistory(connection, transaction, read.Bundle!.ArchiveSha256);
            var evaluation = Evaluate(connection, transaction, read.Bundle);
            if (existingHistory is not null)
            {
                if (previewDigest != existingHistory.PreviewDigest && previewDigest != evaluation.PreviewDigest)
                    return CompleteWithoutWrite(transaction, ResultFailure("preview_changed"));
                return CompleteWithoutWrite(transaction, ToResult(existingHistory, idempotentReplay: true));
            }
            if (evaluation.Conflicts.Count != 0)
                return CompleteWithoutWrite(transaction, ResultFailure("record_conflict"));
            if (previewDigest != evaluation.PreviewDigest)
                return CompleteWithoutWrite(transaction, ResultFailure("preview_changed"));

            var importedAt = timeProvider.GetUtcNow().ToUniversalTime();
            InsertHistory(connection, transaction, read.Bundle, evaluation, importedAt);
            foreach (var state in evaluation.RecordStates.Where(item => item.State == "new"))
                InsertRecord(connection, transaction, state.Record, importedAt);
            checkpoint?.Invoke("after_records");
            foreach (var record in read.Bundle.Records)
                InsertOrigin(connection, transaction, read.Bundle, record, importedAt);
            checkpoint?.Invoke("after_origins");
            foreach (var node in read.Bundle.GraphNodes)
                UpsertNode(connection, transaction, read.Bundle.ArchiveSha256, node, evaluation.EffectiveNodeStates[node.LocalNodeId]);
            foreach (var node in read.Bundle.GraphNodes)
                SetEdgesTargetingResolution(connection, transaction, node.LocalNodeId,
                    evaluation.EffectiveNodeStates[node.LocalNodeId] == "defined"
                        ? "resolved"
                        : evaluation.EffectiveNodeStates[node.LocalNodeId]);
            foreach (var edge in read.Bundle.GraphEdges.Where(item => evaluation.NewRecordIds.Contains(item.SourceRecordLocalId)))
                InsertEdge(connection, transaction, read.Bundle.ArchiveSha256, edge,
                    evaluation.EffectiveNodeStates[edge.TargetNodeId] == "defined" ? "resolved" : evaluation.EffectiveNodeStates[edge.TargetNodeId]);
            checkpoint?.Invoke("after_graph");
            SanitizedImportSchemaV1.Validate(connection, transaction);
            transaction.Commit();
            return new(
                true, null, SanitizedImportContractVersions.Result, read.Bundle.ArchiveSha256,
                read.Bundle.ArchiveSha256, evaluation.PreviewDigest, "committed",
                evaluation.ExpectedChanges.Records, evaluation.RecordStates.Count(item => item.State == "duplicate"),
                evaluation.ExpectedChanges.GraphNodes, evaluation.ExpectedChanges.GraphEdges, 0, Migration,
                read.Bundle.Manifest.SnapshotId, read.Bundle.Manifest.SourceLocalMonitorVersion, importedAt, false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return ResultFailure("import_store_busy");
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException
            or IOException or UnauthorizedAccessException or OverflowException)
        {
            return ResultFailure("import_transaction_failed");
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
            SELECT import_id,archive_sha256,preview_digest,status,new_records,duplicate_records,graph_nodes,graph_edges,
                   raw_retention_items,migration_version,migration_chain,migration_step,migration_chain_sha256,
                   source_snapshot_id,source_local_monitor_version,imported_at
            FROM sanitized_import_history
            ORDER BY imported_at DESC,import_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var items = new List<SanitizedImportHistoryItem>();
        while (reader.Read()) items.Add(ReadHistoryItem(reader));
        transaction.Commit();
        return new(SanitizedImportContractVersions.History, items);
    }

    public SanitizedImportHistoryItem? GetHistory(string importId)
    {
        if (!IsHash(importId)) return null;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        SanitizedImportSchemaV1.Validate(connection, transaction);
        var item = ReadHistory(connection, transaction, importId);
        transaction.Commit();
        return item;
    }

    private static Evaluation Evaluate(SqliteConnection connection, SqliteTransaction transaction, SanitizedImportBundle bundle)
    {
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

        var existingNodes = ReadExistingNodes(connection, transaction, bundle.GraphNodes.Select(item => item.LocalNodeId));
        var effectiveNodeStates = bundle.GraphNodes.ToDictionary(
            item => item.LocalNodeId,
            item => existingNodes.TryGetValue(item.LocalNodeId, out var state) ? StrongerState(state, item.State) : item.State,
            StringComparer.Ordinal);
        var newRecordIds = recordStates.Where(item => item.State == "new").Select(item => item.Record.LocalRecordId).ToHashSet(StringComparer.Ordinal);
        var newNodes = bundle.GraphNodes.Count(item => !existingNodes.ContainsKey(item.LocalNodeId));
        var newEdges = bundle.GraphEdges.Count(item => newRecordIds.Contains(item.SourceRecordLocalId));
        var unresolved = bundle.GraphNodes
            .Where(item => effectiveNodeStates[item.LocalNodeId] != "defined")
            .Select(item => new SanitizedImportUnresolved(item.NodeKind, item.SourceId, effectiveNodeStates[item.LocalNodeId]))
            .OrderBy(item => item.NodeKind, StringComparer.Ordinal).ThenBy(item => item.SourceId, StringComparer.Ordinal).ToArray();
        var historyExists = ScalarLong(connection, transaction,
            "SELECT COUNT(*) FROM sanitized_import_history WHERE import_id=$id;", ("$id", bundle.ArchiveSha256)) != 0;
        var writesRejected = historyExists || conflicts.Count != 0;
        var expected = new SanitizedImportExpectedChanges(
            writesRejected ? 0 : newRecordIds.Count,
            writesRejected ? 0 : bundle.Records.Count,
            writesRejected ? 0 : newNodes,
            writesRejected ? 0 : newEdges,
            writesRejected ? 0 : 1,
            0);
        var digest = PreviewDigest(bundle, recordStates, effectiveNodeStates, expected);
        return new(recordStates, conflicts, unresolved, expected, digest, historyExists, effectiveNodeStates, newRecordIds);
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
        evaluation.RecordStates.Count(item => item.State == "new"),
        evaluation.RecordStates.Count(item => item.State == "duplicate"),
        evaluation.RecordStates.Count(item => item.State == "conflict"),
        evaluation.Conflicts,
        evaluation.Unresolved.Length,
        evaluation.Unresolved.Take(SanitizedImportLimits.MaximumPreviewUnresolved).ToArray(),
        evaluation.ExpectedChanges,
        evaluation.Conflicts.Count == 0);

    internal static SanitizedImportPreview Failure(string code) => new(
        false, code, SanitizedImportContractVersions.Preview, null, null, null, null, null, null,
        "unsupported", Migration, null, null, null, [], null, null, [],
        new Dictionary<string, int>(), null, new Dictionary<string, int>(), new Dictionary<string, int>(),
        new Dictionary<string, int>(), new Dictionary<string, string>(), 0, 0, 0, 0, 0, [], 0, [],
        new(0, 0, 0, 0, 0, 0), false);

    internal static SanitizedImportResult ResultFailure(string code) => new(
        false, code, SanitizedImportContractVersions.Result, null, null, null, null,
        0, 0, 0, 0, 0, Migration, null, null, null, false);

    private static SanitizedImportResult CompleteWithoutWrite(SqliteTransaction transaction, SanitizedImportResult result)
    {
        transaction.Commit();
        return result;
    }

    private static SanitizedImportResult ToResult(SanitizedImportHistoryItem item, bool idempotentReplay) => new(
        true, null, SanitizedImportContractVersions.Result, item.ImportId, item.ArchiveSha256, item.PreviewDigest,
        item.Status, item.NewRecords, item.DuplicateRecords, item.GraphNodes, item.GraphEdges,
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
            command.CommandText = "SELECT canonical_sha256,canonical_json FROM sanitized_import_records WHERE record_type=$type AND source_record_id=$id;";
            command.Parameters.AddWithValue("$type", record.RecordType);
            command.Parameters.AddWithValue("$id", record.RecordId);
            using var reader = command.ExecuteReader();
            if (reader.Read()) result[(record.RecordType, record.RecordId)] = new(reader.GetString(0), (byte[])reader[1]);
        }
        return result;
    }

    private static Dictionary<string, string> ReadExistingNodes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> nodeIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in nodeIds.Chunk(400))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = batch.Select((_, index) => $"$id{index}").ToArray();
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            command.CommandText = $"SELECT local_node_id,state FROM sanitized_import_graph_nodes WHERE local_node_id IN ({string.Join(',', names)});";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static SanitizedImportHistoryItem? ReadHistory(SqliteConnection connection, SqliteTransaction transaction, string importId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT import_id,archive_sha256,preview_digest,status,new_records,duplicate_records,graph_nodes,graph_edges,
                   raw_retention_items,migration_version,migration_chain,migration_step,migration_chain_sha256,
                   source_snapshot_id,source_local_monitor_version,imported_at
            FROM sanitized_import_history WHERE import_id=$id;
            """;
        command.Parameters.AddWithValue("$id", importId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHistoryItem(reader) : null;
    }

    private static SanitizedImportHistoryItem ReadHistoryItem(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
        reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8),
        new(reader.GetInt32(9), reader.GetString(10), reader.GetString(11), reader.GetString(12), false),
        reader.GetString(13), reader.GetString(14), ParseTimestamp(reader.GetString(15)));

    private static void InsertHistory(SqliteConnection connection, SqliteTransaction transaction, SanitizedImportBundle bundle, Evaluation evaluation, DateTimeOffset importedAt) =>
        Execute(connection, transaction, """
            INSERT INTO sanitized_import_history(
                import_id,archive_sha256,preview_digest,status,new_records,duplicate_records,graph_nodes,graph_edges,
                raw_retention_items,migration_version,migration_chain,migration_step,migration_chain_sha256,
                source_snapshot_id,source_local_monitor_version,imported_at)
            VALUES($id,$archive,$preview,'committed',$new,$duplicate,$nodes,$edges,0,1,$chain,$step,$chain_hash,$snapshot,$monitor,$at);
            """,
            ("$id", bundle.ArchiveSha256), ("$archive", bundle.ArchiveSha256), ("$preview", evaluation.PreviewDigest),
            ("$new", evaluation.ExpectedChanges.Records), ("$duplicate", evaluation.RecordStates.Count(item => item.State == "duplicate")),
            ("$nodes", evaluation.ExpectedChanges.GraphNodes), ("$edges", evaluation.ExpectedChanges.GraphEdges),
            ("$chain", SanitizedImportContractVersions.MigrationChain), ("$step", SanitizedImportContractVersions.MigrationStep),
            ("$chain_hash", SanitizedImportContractVersions.MigrationChainSha256), ("$snapshot", bundle.Manifest.SnapshotId),
            ("$monitor", bundle.Manifest.SourceLocalMonitorVersion), ("$at", Timestamp(importedAt)));

    private static void InsertRecord(SqliteConnection connection, SqliteTransaction transaction, SanitizedImportRecord record, DateTimeOffset importedAt) =>
        Execute(connection, transaction, "INSERT INTO sanitized_import_records(local_record_id,record_type,source_record_id,canonical_sha256,canonical_json,created_at) VALUES($local,$type,$source,$hash,$json,$at);",
            ("$local", record.LocalRecordId), ("$type", record.RecordType), ("$source", record.RecordId),
            ("$hash", record.CanonicalSha256), ("$json", record.CanonicalBytes), ("$at", Timestamp(importedAt)));

    private static void InsertOrigin(SqliteConnection connection, SqliteTransaction transaction, SanitizedImportBundle bundle, SanitizedImportRecord record, DateTimeOffset importedAt) =>
        Execute(connection, transaction, "INSERT INTO sanitized_import_origins(import_id,local_record_id,entry_path,source_snapshot_id,source_local_monitor_version,source_created_at,imported_at) VALUES($import,$record,$path,$snapshot,$monitor,$created,$at);",
            ("$import", bundle.ArchiveSha256), ("$record", record.LocalRecordId), ("$path", record.EntryPath),
            ("$snapshot", bundle.Manifest.SnapshotId), ("$monitor", bundle.Manifest.SourceLocalMonitorVersion),
            ("$created", Timestamp(bundle.Manifest.CreatedAt)), ("$at", Timestamp(importedAt)));

    private static void UpsertNode(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importId,
        SanitizedImportGraphNode node,
        string effectiveState)
    {
        Execute(connection, transaction, """
            INSERT INTO sanitized_import_graph_nodes(local_node_id,node_kind,source_id,state,defining_record_local_id,first_import_id)
            VALUES($id,$kind,$source,$effective,$record,$import)
            ON CONFLICT(local_node_id) DO UPDATE SET
                state=$effective,
                defining_record_local_id=CASE
                    WHEN sanitized_import_graph_nodes.state='defined' THEN sanitized_import_graph_nodes.defining_record_local_id
                    WHEN $effective='defined' THEN excluded.defining_record_local_id
                    ELSE sanitized_import_graph_nodes.defining_record_local_id
                END;
            """,
            ("$id", node.LocalNodeId), ("$kind", node.NodeKind), ("$source", node.SourceId),
            ("$effective", effectiveState), ("$record", node.DefiningRecordLocalId), ("$import", importId));
    }

    private static void InsertEdge(SqliteConnection connection, SqliteTransaction transaction, string importId, SanitizedImportGraphEdge edge, string resolution) =>
        Execute(connection, transaction, "INSERT INTO sanitized_import_graph_edges(local_edge_id,source_record_local_id,source_node_id,target_node_id,relation,edge_ordinal,resolution_state,provenance_json,first_import_id) VALUES($id,$record,$source,$target,$relation,$ordinal,$resolution,$provenance,$import);",
            ("$id", edge.LocalEdgeId), ("$record", edge.SourceRecordLocalId), ("$source", edge.SourceNodeId),
            ("$target", edge.TargetNodeId), ("$relation", edge.Relation), ("$ordinal", edge.Ordinal),
            ("$resolution", resolution), ("$provenance", edge.ProvenanceJson), ("$import", importId));

    private static void SetEdgesTargetingResolution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string localNodeId,
        string resolution) =>
        Execute(connection, transaction,
            "UPDATE sanitized_import_graph_edges SET resolution_state=$resolution WHERE target_node_id=$target AND resolution_state!=$resolution;",
            ("$resolution", resolution), ("$target", localNodeId));

    private static string StrongerState(string first, string second) => StateRank(first) >= StateRank(second) ? first : second;
    private static int StateRank(string state) => state switch { "defined" => 3, "missing" => 2, _ => 1 };

    private static string PreviewDigest(
        SanitizedImportBundle bundle,
        IReadOnlyList<RecordState> records,
        IReadOnlyDictionary<string, string> nodeStates,
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
        foreach (var item in nodeStates.OrderBy(item => item.Key, StringComparer.Ordinal)) { Append(hash, item.Key); Append(hash, item.Value); }
        foreach (var edge in bundle.GraphEdges.OrderBy(item => item.LocalEdgeId, StringComparer.Ordinal))
        {
            Append(hash, edge.LocalEdgeId);
            Append(hash, nodeStates[edge.TargetNodeId] == "defined" ? "resolved" : nodeStates[edge.TargetNodeId]);
        }
        foreach (var value in new[] { expected.Records, expected.Origins, expected.GraphNodes, expected.GraphEdges, expected.HistoryRows, expected.RawRetentionItems })
            Append(hash, value.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
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
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value,
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed record ExistingRecord(string Sha256, byte[] Bytes);
    private sealed record RecordState(SanitizedImportRecord Record, string State, string? ExistingSha256);
    private sealed record Evaluation(
        IReadOnlyList<RecordState> RecordStates,
        IReadOnlyList<SanitizedImportConflict> Conflicts,
        SanitizedImportUnresolved[] Unresolved,
        SanitizedImportExpectedChanges ExpectedChanges,
        string PreviewDigest,
        bool HistoryExists,
        IReadOnlyDictionary<string, string> EffectiveNodeStates,
        IReadOnlySet<string> NewRecordIds);
}

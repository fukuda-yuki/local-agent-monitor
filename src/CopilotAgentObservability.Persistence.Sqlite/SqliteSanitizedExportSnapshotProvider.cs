using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.SanitizedExport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

public sealed class SqliteSanitizedExportSnapshotProvider : ISanitizedExportSnapshotProvider
{
    private const int BusyTimeoutMilliseconds = 5000;
    private static readonly string[] MonitorTraceColumns =
    [
        "id", "trace_id", "client_kind", "experiment_id", "task_id", "task_category", "agent_variant", "prompt_version",
        "span_count", "tool_call_count", "error_count", "first_seen_at", "last_seen_at", "projected_at", "input_tokens",
        "output_tokens", "total_tokens", "turn_count", "agent_invocation_count", "duration_ms", "primary_model", "repository_name",
        "workspace_label", "repo_snapshot", "cache_read_tokens", "cache_creation_tokens", "trace_status",
    ];
    private static readonly string[] SessionColumns =
    [
        "session_id", "status", "completeness", "repository", "workspace", "started_at", "ended_at", "last_seen_at",
        "raw_retention_state", "created_at", "updated_at",
    ];
    private static readonly string[] SessionRunColumns =
    [
        "run_id", "session_id", "source_surface", "native_run_id", "trace_id", "parent_run_id", "model", "started_at",
        "ended_at", "input_tokens", "output_tokens", "total_tokens", "status",
    ];
    private static readonly string[] SessionEventColumns =
    [
        "event_id", "session_id", "run_id", "source_surface", "parent_event_id", "trace_id", "status", "source_adapter",
        "source_event_id", "type", "occurred_at", "content_state", "source_application_version", "adapter_version",
        "schema_fingerprint", "normalization_version", "match_kind",
    ];
    private static readonly string[] FindingColumns = ["analysis_run_id", "schema_version", "payload_json", "payload_sha256", "created_at"];
    private readonly string databasePath;

    public SqliteSanitizedExportSnapshotProvider(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
    }

    public SanitizedExportSnapshotCapture Capture(SanitizedExportSelection selection)
    {
        if (!SanitizedExportSelectionValidator.IsValid(selection)) return Failure("invalid_selection");
        try
        {
            using var connection = Open();
            Execute(connection, null, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
            Execute(connection, null, "PRAGMA query_only=ON;");
            using var transaction = connection.BeginTransaction(deferred: true);
            if (!ValidateRequiredSchemas(connection, transaction)) return Failure("snapshot_store_unavailable");

            var findingState = OptionalTableState(connection, transaction, "instruction_finding_handoffs", FindingColumns);
            if (findingState == OptionalState.Invalid) return Failure("snapshot_store_unavailable");
            var alertState = ValidateAlertState(connection, transaction);
            if (alertState == OptionalState.Invalid) return Failure("snapshot_store_unavailable");

            var descriptors = ReadDescriptors(connection, transaction, selection, findingState, alertState);
            if (descriptors.Count > SanitizedExportLimits.MaximumRecords) return Failure("selection_limit_exceeded");
            if (descriptors.Any(item => item.Length is <= 0 or > SanitizedExportLimits.MaximumRecordBytes)
                || descriptors.Where(item => item.Type == "instruction_finding_handoff").Any(item => item.Length > InstructionFindingHandoffConsumerV1.MaxPayloadBytes)
                || descriptors.Sum(item => (long)item.Length) > SanitizedExportLimits.MaximumUncompressedBytes)
                return Failure("uncompressed_size_limit_exceeded");

            var records = Materialize(connection, transaction, descriptors, selection);
            var agentVersions = ReadAgentVersions(connection, transaction, records);
            var processingVersions = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["monitor_schema"] = MonitorSchemaMigrator.BaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
                ["session_schema"] = "13",
                ["sanitized_export"] = "1",
            };
            if (alertState == OptionalState.Valid) processingVersions["alert_engine_schema"] = "1";

            var capabilities = new SanitizedExportCapabilityStates(
                records.Any(record => record.RecordType == "instruction_finding_handoff") ? "available" : "missing",
                records.Any(record => record.RecordType == "alert_receipt") ? "available" : "missing",
                "unavailable", "unavailable", "unavailable");
            var snapshotId = SnapshotId(records, agentVersions, processingVersions, capabilities);
            transaction.Commit();
            return new(true, null, new(snapshotId, "local-monitor.v1", agentVersions, records, capabilities, processingVersions));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure("snapshot_store_busy");
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FormatException or OverflowException or JsonException
            or AlertReceiptConsumerException or InstructionFindingHandoffConsumerValidationException)
        {
            return Failure("snapshot_store_unavailable");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool ValidateRequiredSchemas(SqliteConnection connection, SqliteTransaction transaction) =>
        Version(connection, transaction, "monitor") == MonitorSchemaMigrator.BaseSchemaVersion
        && Version(connection, transaction, "session") == 13
        && ExactColumns(connection, transaction, "monitor_traces", MonitorTraceColumns)
        && ExactColumns(connection, transaction, "sessions", SessionColumns)
        && ExactColumns(connection, transaction, "session_runs", SessionRunColumns)
        && ExactColumns(connection, transaction, "session_events", SessionEventColumns);

    private static OptionalState ValidateAlertState(SqliteConnection connection, SqliteTransaction transaction)
    {
        var present = AlertSchemaV1.AnyEngineTableExists(connection, transaction) || Version(connection, transaction, AlertSchemaV1.Component) is not null;
        if (!present) return OptionalState.Absent;
        return AlertSchemaV1.IsValid(connection, transaction) ? OptionalState.Valid : OptionalState.Invalid;
    }

    private static OptionalState OptionalTableState(SqliteConnection connection, SqliteTransaction transaction, string table, string[] columns)
    {
        if (!TableExists(connection, transaction, table)) return OptionalState.Absent;
        return ExactColumns(connection, transaction, table, columns) ? OptionalState.Valid : OptionalState.Invalid;
    }

    private static List<Descriptor> ReadDescriptors(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SanitizedExportSelection selection,
        OptionalState findings,
        OptionalState alerts)
    {
        var selectableBranches = new List<string>
        {
            """
            SELECT DISTINCT 'repository_metadata_projection' AS record_type,
                   m.trace_id AS source_id,
                   s.session_id, m.trace_id, r.source_surface,
                   m.repository_name, m.workspace_label, m.repo_snapshot,
                   COALESCE(m.last_seen_at,m.projected_at) AS observed_at,
                   COALESCE(s.completeness,'unknown') AS completeness,
                   COALESCE((SELECT e.content_state FROM session_events e WHERE e.session_id=s.session_id ORDER BY e.occurred_at DESC,e.event_id DESC LIMIT 1),'unknown') AS content_state,
                   COALESCE(s.raw_retention_state,'unknown') AS retention_state,
                   1 AS payload_length
            FROM monitor_traces m
            LEFT JOIN session_runs r ON r.trace_id=m.trace_id
            LEFT JOIN sessions s ON s.session_id=r.session_id
            """,
        };
        if (findings == OptionalState.Valid)
            selectableBranches.Add("SELECT 'instruction_finding_handoff', CAST(analysis_run_id AS TEXT), NULL,NULL,NULL,NULL,NULL,NULL,created_at,'unknown','unknown','unknown',length(CAST(payload_json AS BLOB)) FROM instruction_finding_handoffs");

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var predicate = BuildPredicate(selection, command);
        var selectedSql = $"SELECT * FROM ({string.Join(" UNION ALL ", selectableBranches)}) d WHERE {predicate}";
        var includeAlerts = alerts == OptionalState.Valid && (selection.ReceiptTypes is not { Count: > 0 } || selection.ReceiptTypes.Contains("alert_receipt", StringComparer.Ordinal));
        var opaqueAlerts = includeAlerts
            ? " UNION ALL SELECT 'alert_receipt',alert_id,NULL,NULL,NULL,NULL,NULL,NULL,'0001-01-01T00:00:00.0000000Z','unknown','unknown','unknown',length(CAST(canonical_json AS BLOB)) FROM alert_receipts"
            : string.Empty;
        command.CommandText = $"SELECT * FROM ({selectedSql}{opaqueAlerts}) ORDER BY observed_at COLLATE BINARY,record_type COLLATE BINARY,source_id COLLATE BINARY LIMIT {SanitizedExportLimits.MaximumRecords + 1};";
        using var reader = command.ExecuteReader();
        var result = new List<Descriptor>();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            var sourceId = reader.GetString(1);
            var sessionId = Nullable(reader, 2);
            var traceId = Nullable(reader, 3);
            var sourceSurface = Nullable(reader, 4);
            var repositoryName = Nullable(reader, 5);
            var workspaceLabel = Nullable(reader, 6);
            var repoSnapshot = Nullable(reader, 7);
            var observedAt = Timestamp(reader.GetString(8));
            var recordId = type == "repository_metadata_projection"
                ? FramedHash("repository-metadata-projection.v1", sessionId ?? string.Empty, traceId ?? string.Empty, sourceSurface ?? string.Empty)
                : sourceId;
            var descriptor = new Descriptor(type, sourceId, recordId, sessionId, traceId, sourceSurface, repositoryName, workspaceLabel,
                repoSnapshot, observedAt, reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetInt32(12));
            if (!ValidDescriptorMetadata(descriptor)) throw new InvalidOperationException();
            if (type == "repository_metadata_projection" && result.FirstOrDefault(item => item.RecordId == recordId) is { } existing)
            {
                if (existing != descriptor) throw new InvalidOperationException();
                continue;
            }
            result.Add(descriptor);
        }
        if (result.Where(item => item.Type == "repository_metadata_projection" && item.SessionId is not null)
            .GroupBy(item => item.TraceId, StringComparer.Ordinal)
            .Any(group => group.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            throw new InvalidOperationException();
        return result;
    }

    private static string BuildPredicate(SanitizedExportSelection selection, SqliteCommand command)
    {
        var clauses = new List<string>();
        var ids = new List<string>();
        ids.AddRange(InClause(command, "session_id", selection.SessionIds, "sid"));
        ids.AddRange(InClause(command, "trace_id", selection.TraceIds, "tid"));
        if (ids.Count > 0) clauses.Add("(" + string.Join(" OR ", ids) + ")");
        clauses.AddRange(InClause(command, "source_surface", selection.SourceSurfaces, "surface"));
        clauses.AddRange(InClause(command, "repository_name", selection.RepositoryNames, "repository"));
        clauses.AddRange(InClause(command, "workspace_label", selection.WorkspaceLabels, "workspace"));
        clauses.AddRange(InClause(command, "record_type", selection.ReceiptTypes, "receipt"));
        if (selection.StartInclusive is { } start)
        {
            command.Parameters.AddWithValue("$start", Wire(start));
            clauses.Add("observed_at >= $start COLLATE BINARY");
        }
        if (selection.EndExclusive is { } end)
        {
            command.Parameters.AddWithValue("$end", Wire(end));
            clauses.Add("observed_at < $end COLLATE BINARY");
        }
        return clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses);
    }

    private static IEnumerable<string> InClause(SqliteCommand command, string column, IReadOnlyList<string>? values, string prefix)
    {
        if (values is not { Count: > 0 }) yield break;
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            names[index] = $"${prefix}{index}";
            command.Parameters.AddWithValue(names[index], values[index]);
        }
        yield return $"{column} COLLATE BINARY IN ({string.Join(',', names)})";
    }

    private static IReadOnlyList<SanitizedExportRecord> Materialize(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Descriptor> descriptors, SanitizedExportSelection selection)
    {
        var records = new List<SanitizedExportRecord>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            var materialized = descriptor;
            byte[] bytes;
            if (descriptor.Type == "repository_metadata_projection")
            {
                bytes = RepositoryMetadataProjectionV1.Serialize(descriptor.RecordId, descriptor.SessionId, descriptor.TraceId,
                    descriptor.SourceSurface, descriptor.RepositoryName, descriptor.WorkspaceLabel, descriptor.RepoSnapshot,
                    descriptor.ObservedAt, descriptor.Completeness, descriptor.ContentState, descriptor.RetentionState);
            }
            else if (descriptor.Type == "instruction_finding_handoff")
            {
                var stored = ReadCarrier(connection, transaction, "instruction_finding_handoffs", "analysis_run_id", descriptor.SourceId,
                    "schema_version,payload_json,payload_sha256");
                bytes = Encoding.UTF8.GetBytes(stored[1]);
                if (stored[0] != "instruction-finding-handoff.v1" || Hash(bytes) != stored[2]
                    || InstructionFindingHandoffConsumerV1.Validate(bytes) != long.Parse(descriptor.SourceId, CultureInfo.InvariantCulture))
                    throw new InvalidOperationException();
            }
            else
            {
                var stored = ReadCarrier(connection, transaction, "alert_receipts", "alert_id", descriptor.SourceId, "schema_version,canonical_json");
                bytes = Encoding.UTF8.GetBytes(stored[1]);
                var envelope = AlertReceiptConsumerV1.Validate(bytes);
                if (stored[0] != "alert.receipt.v1" || envelope.AlertId != descriptor.RecordId)
                    throw new InvalidOperationException();
                materialized = descriptor with
                {
                    SessionId = envelope.SessionId,
                    TraceId = envelope.TraceId,
                    SourceSurface = envelope.SourceSurface,
                    ObservedAt = envelope.LastObservedAt,
                };
                if (!MatchesSelection(materialized, selection)) continue;
            }
            if (bytes.Length != descriptor.Length && descriptor.Type != "repository_metadata_projection") throw new InvalidOperationException();
            records.Add(new(EntryPath(materialized.Type, materialized.RecordId), materialized.Type, materialized.RecordId,
                materialized.SessionId, materialized.TraceId, materialized.SourceSurface, materialized.RepositoryName, materialized.WorkspaceLabel,
                materialized.RepoSnapshot, materialized.ObservedAt, bytes, [], materialized.Completeness, materialized.ContentState, materialized.RetentionState));
        }
        return records.OrderBy(record => record.ObservedAt).ThenBy(record => record.RecordType, StringComparer.Ordinal).ThenBy(record => record.RecordId, StringComparer.Ordinal).ToArray();
    }

    private static string[] ReadCarrier(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string id, string columns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {columns} FROM {table} WHERE {key}=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException();
        var result = Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray();
        if (reader.Read()) throw new InvalidOperationException();
        return result;
    }

    private static IReadOnlyList<SanitizedExportAgentVersion> ReadAgentVersions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SanitizedExportRecord> records)
    {
        var selectedSessions = records.Select(record => record.SessionId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (selectedSessions.Length == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = selectedSessions.Select((_, index) => $"$session{index}").ToArray();
        for (var index = 0; index < selectedSessions.Length; index++) command.Parameters.AddWithValue(names[index], selectedSessions[index]);
        command.CommandText = $"SELECT DISTINCT source_surface,source_application_version FROM session_events WHERE session_id IN ({string.Join(',', names)}) AND source_surface IS NOT NULL AND source_application_version IS NOT NULL ORDER BY source_surface COLLATE BINARY,source_application_version COLLATE BINARY LIMIT 257;";
        using var reader = command.ExecuteReader();
        var result = new List<SanitizedExportAgentVersion>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
        if (result.Count > SanitizedExportLimits.MaximumVersions) throw new InvalidOperationException();
        if (result.Any(item => !BoundedToken(item.SourceSurface) || !BoundedToken(item.Version))) throw new InvalidOperationException();
        return result;
    }

    private static bool MatchesSelection(Descriptor item, SanitizedExportSelection selection)
    {
        var hasIds = selection.SessionIds is { Count: > 0 } || selection.TraceIds is { Count: > 0 };
        if (hasIds && !(selection.SessionIds?.Contains(item.SessionId!, StringComparer.Ordinal) == true
            || selection.TraceIds?.Contains(item.TraceId!, StringComparer.Ordinal) == true)) return false;
        if (selection.SourceSurfaces is { Count: > 0 } && !selection.SourceSurfaces.Contains(item.SourceSurface!, StringComparer.Ordinal)) return false;
        if (selection.RepositoryNames is { Count: > 0 } && !selection.RepositoryNames.Contains(item.RepositoryName!, StringComparer.Ordinal)) return false;
        if (selection.WorkspaceLabels is { Count: > 0 } && !selection.WorkspaceLabels.Contains(item.WorkspaceLabel!, StringComparer.Ordinal)) return false;
        if (selection.ReceiptTypes is { Count: > 0 } && !selection.ReceiptTypes.Contains(item.Type, StringComparer.Ordinal)) return false;
        return !(selection.StartInclusive is { } start && item.ObservedAt < start)
            && !(selection.EndExclusive is { } end && item.ObservedAt >= end);
    }

    private static string SnapshotId(
        IReadOnlyList<SanitizedExportRecord> records,
        IReadOnlyList<SanitizedExportAgentVersion> versions,
        IReadOnlyDictionary<string, string> processingVersions,
        SanitizedExportCapabilityStates capabilities)
    {
        var value = new List<string> { "sanitized-export-snapshot.v1", records.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var record in records)
        {
            value.AddRange(["record", record.EntryPath, record.RecordType, record.RecordId]);
            AddNullable(value, record.SessionId);
            AddNullable(value, record.TraceId);
            AddNullable(value, record.SourceSurface);
            AddNullable(value, record.RepositoryName);
            AddNullable(value, record.WorkspaceLabel);
            AddNullable(value, record.RepoSnapshot);
            value.AddRange([record.ObservedAt.ToString("O", CultureInfo.InvariantCulture), record.Completeness,
                record.ContentState, record.RetentionState, Hash(record.CanonicalBytes),
                record.Dependencies.Count.ToString(CultureInfo.InvariantCulture)]);
            foreach (var dependency in record.Dependencies)
                value.AddRange(["dependency", dependency.RecordType, dependency.RecordId, dependency.Disposition.ToString()]);
        }
        value.AddRange(["capabilities", capabilities.InstructionFindings, capabilities.AlertReceipts,
            capabilities.HistoricalInstructionAnalysis, capabilities.HistoricalEfficiencyAnalysis, capabilities.AlertCenter]);
        value.Add(versions.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var version in versions) value.AddRange(["agent_version", version.SourceSurface, version.Version]);
        value.Add(processingVersions.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var version in processingVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
            value.AddRange(["processing_version", version.Key, version.Value]);
        return FramedHash([.. value]);
    }

    private static void AddNullable(List<string> values, string? value)
    {
        values.Add(value is null ? "null" : "value");
        if (value is not null) values.Add(value);
    }

    private static bool ValidDescriptorMetadata(Descriptor item) =>
        BoundedToken(item.SourceId) && BoundedToken(item.RecordId)
        && BoundedNullableToken(item.SessionId) && BoundedNullableToken(item.TraceId) && BoundedNullableToken(item.SourceSurface)
        && BoundedNullableToken(item.RepositoryName) && BoundedNullableToken(item.WorkspaceLabel) && BoundedNullableToken(item.RepoSnapshot)
        && BoundedToken(item.Completeness) && BoundedToken(item.ContentState) && BoundedToken(item.RetentionState);

    private static bool BoundedNullableToken(string? value) => value is null || BoundedToken(value);
    private static bool BoundedToken(string value) => value.Length is > 0 and <= SanitizedExportLimits.MaximumIdentifierLength
        && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#');

    private static string EntryPath(string type, string id) => type switch
    {
        "repository_metadata_projection" => $"repository-metadata/{id}.json",
        "instruction_finding_handoff" => $"instruction-findings/{id}.json",
        _ => $"alert-receipts/{id}.json",
    };

    private static long? Version(SqliteConnection connection, SqliteTransaction transaction, string component)
    {
        if (!TableExists(connection, transaction, "schema_version")) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_version WHERE component=$component;";
        command.Parameters.AddWithValue("$component", component);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool ExactColumns(SqliteConnection connection, SqliteTransaction transaction, string table, IReadOnlyList<string> expected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var reader = command.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read()) actual.Add(reader.GetString(0));
        return actual.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset Timestamp(string value) => DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static string Wire(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    internal static string FramedHash(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return Hash(stream.ToArray());
    }
    private static SanitizedExportSnapshotCapture Failure(string code) => new(false, code, null);

    private enum OptionalState { Absent, Valid, Invalid }
    private sealed record Descriptor(
        string Type, string SourceId, string RecordId, string? SessionId, string? TraceId, string? SourceSurface,
        string? RepositoryName, string? WorkspaceLabel, string? RepoSnapshot, DateTimeOffset ObservedAt,
        string Completeness, string ContentState, string RetentionState, int Length);
}

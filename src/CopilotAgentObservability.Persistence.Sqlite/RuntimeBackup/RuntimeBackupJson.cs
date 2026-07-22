using System.Globalization;
using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

public static class RuntimeBackupJson
{
    public static byte[] SerializeResult<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    internal static byte[] WriteManifest(RuntimeBackupManifestData value)
    {
        ValidateSemanticContract(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", RuntimeBackupContractVersions.Manifest);
            writer.WriteString("bundle_schema_version", RuntimeBackupContractVersions.BundleSchema);
            writer.WriteString("bundle_profile", RuntimeBackupContractVersions.BundleProfile);
            writer.WriteString("created_at", Timestamp(value.CreatedAt));
            writer.WriteString("source_application_version", value.SourceApplicationVersion);
            writer.WriteString("source_platform", value.SourcePlatform);
            writer.WritePropertyName("snapshot"); writer.WriteStartObject();
            writer.WriteString("method", "sqlite_online_backup");
            writer.WriteString("source_journal_mode", value.SourceJournalMode);
            writer.WriteString("integrity_check", "ok");
            writer.WriteString("foreign_key_check", "ok");
            writer.WriteString("snapshot_id", value.DatabaseSha256);
            writer.WriteEndObject();
            writer.WritePropertyName("backup_window"); writer.WriteStartObject();
            writer.WriteString("started_at", Timestamp(value.BackupWindow.StartedAt));
            writer.WriteString("completed_at", Timestamp(value.BackupWindow.CompletedAt));
            WriteNullableLongMap(writer, "projection_cursors_at_start", value.BackupWindow.ProjectionCursorsAtStart);
            WriteNullableLongMap(writer, "projection_cursors_at_end", value.BackupWindow.ProjectionCursorsAtEnd);
            writer.WriteEndObject();
            WriteIntMap(writer, "component_versions", value.ComponentVersions);
            WriteLongMap(writer, "row_counts", value.RowCounts);
            WriteNullableLongMap(writer, "projection_cursors", value.ProjectionCursors);
            writer.WritePropertyName("retention"); writer.WriteStartObject();
            WriteLongMap(writer, "store_kind_counts", value.Retention.StoreKindCounts);
            WriteLongMap(writer, "state_counts", value.Retention.StateCounts);
            writer.WriteNumber("tombstone_count", value.Retention.TombstoneCount);
            WriteNullable(writer, "earliest_captured_at", value.Retention.EarliestCapturedAt);
            WriteNullable(writer, "latest_captured_at", value.Retention.LatestCapturedAt);
            WriteNullable(writer, "earliest_expires_at", value.Retention.EarliestExpiresAt);
            WriteNullable(writer, "latest_expires_at", value.Retention.LatestExpiresAt);
            writer.WritePropertyName("policies"); writer.WriteStartArray(); foreach (var policy in value.Retention.Policies.Order(StringComparer.Ordinal)) writer.WriteStringValue(policy); writer.WriteEndArray();
            writer.WriteString("backup_non_purge_warning_code", RuntimeBackupWarnings.RetentionBackupNotPurged);
            writer.WriteEndObject();
            writer.WritePropertyName("external_state"); writer.WriteStartArray();
            foreach (var state in value.ExternalState)
            {
                writer.WriteStartObject(); writer.WriteString("kind", state.Kind); writer.WriteString("source_state", state.SourceState);
                writer.WriteBoolean("included", state.Included); writer.WriteString("consistency", state.Consistency); writer.WriteString("restore_action", state.RestoreAction); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("files"); writer.WriteStartArray(); writer.WriteStartObject();
            writer.WriteString("path", "database.sqlite"); writer.WriteNumber("size", value.DatabaseSize); writer.WriteString("sha256", value.DatabaseSha256);
            writer.WriteEndObject(); writer.WriteEndArray();
            writer.WritePropertyName("warnings"); writer.WriteStartArray(); foreach (var warning in RuntimeBackupWarnings.All) writer.WriteStringValue(warning); writer.WriteEndArray();
            writer.WritePropertyName("compatibility"); writer.WriteStartObject();
            writer.WriteNumber("minimum_reader_version", 1); writer.WriteNumber("maximum_reader_version", 1);
            writer.WriteString("migration_policy", "supported_older_only");
            writer.WritePropertyName("required_components"); writer.WriteStartObject();
            foreach (var item in value.ComponentVersions.OrderBy(item => item.Key, StringComparer.Ordinal)) writer.WriteNumber(item.Key, item.Value);
            writer.WriteEndObject(); writer.WriteEndObject();
            writer.WriteEndObject();
        }
        if (stream.Length > RuntimeBackupLimits.MaximumManifestBytes)
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
        return stream.ToArray();
    }

    internal static RuntimeBackupManifestData ParseManifest(byte[] bytes)
    {
        if (bytes.Length is 0 or > RuntimeBackupLimits.MaximumManifestBytes) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = RuntimeBackupLimits.MaximumJsonDepth });
            var root = document.RootElement;
            if (!HasExactProperties(root, "schema_version", "bundle_schema_version", "bundle_profile", "created_at", "source_application_version", "source_platform", "snapshot", "backup_window", "component_versions", "row_counts", "projection_cursors", "retention", "external_state", "files", "warnings", "compatibility")
                || !FixedString(root, "schema_version", RuntimeBackupContractVersions.Manifest)
                || !FixedString(root, "bundle_schema_version", RuntimeBackupContractVersions.BundleSchema)
                || !FixedString(root, "bundle_profile", RuntimeBackupContractVersions.BundleProfile)
                || !TryCanonicalUtcTimestamp(root.GetProperty("created_at"), out var createdAt)
                || !TryBoundedString(root.GetProperty("source_application_version"), MaximumStringTokenLength, out var sourceApplicationVersion)
                || !TryBoundedString(root.GetProperty("source_platform"), MaximumStringTokenLength, out var sourcePlatform))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            var snapshot = root.GetProperty("snapshot");
            if (!HasExactProperties(snapshot, "method", "source_journal_mode", "integrity_check", "foreign_key_check", "snapshot_id")
                || !FixedString(snapshot, "method", "sqlite_online_backup")
                || !TryBoundedString(snapshot.GetProperty("source_journal_mode"), MaximumStringTokenLength, out var sourceJournalMode)
                || !FixedString(snapshot, "integrity_check", "ok")
                || !FixedString(snapshot, "foreign_key_check", "ok")
                || !TrySha256(snapshot.GetProperty("snapshot_id"), out var snapshotId))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            var backupWindow = root.GetProperty("backup_window");
            if (!HasExactProperties(backupWindow, "started_at", "completed_at", "projection_cursors_at_start", "projection_cursors_at_end")
                || !TryCanonicalUtcTimestamp(backupWindow.GetProperty("started_at"), out var startedAt)
                || !TryCanonicalUtcTimestamp(backupWindow.GetProperty("completed_at"), out var completedAt)
                || !TryNullableLongMap(backupWindow.GetProperty("projection_cursors_at_start"), out var cursorsAtStart)
                || !TryNullableLongMap(backupWindow.GetProperty("projection_cursors_at_end"), out var cursorsAtEnd)
                || !TryIntMap(root.GetProperty("component_versions"), out var componentVersions)
                || !TryLongMap(root.GetProperty("row_counts"), out var rowCounts)
                || !TryNullableLongMap(root.GetProperty("projection_cursors"), out var projectionCursors))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            var retention = root.GetProperty("retention");
            if (!HasExactProperties(retention, "store_kind_counts", "state_counts", "tombstone_count", "earliest_captured_at", "latest_captured_at", "earliest_expires_at", "latest_expires_at", "policies", "backup_non_purge_warning_code")
                || !TryLongMap(retention.GetProperty("store_kind_counts"), out var storeKindCounts)
                || !TryLongMap(retention.GetProperty("state_counts"), out var stateCounts)
                || retention.GetProperty("tombstone_count").ValueKind != JsonValueKind.Number
                || !retention.GetProperty("tombstone_count").TryGetInt64(out var tombstoneCount)
                || !TryNullableCanonicalUtcTimestamp(retention.GetProperty("earliest_captured_at"), out var earliestCapturedAt)
                || !TryNullableCanonicalUtcTimestamp(retention.GetProperty("latest_captured_at"), out var latestCapturedAt)
                || !TryNullableCanonicalUtcTimestamp(retention.GetProperty("earliest_expires_at"), out var earliestExpiresAt)
                || !TryNullableCanonicalUtcTimestamp(retention.GetProperty("latest_expires_at"), out var latestExpiresAt)
                || !TryStringArray(retention.GetProperty("policies"), RuntimeBackupLimits.MaximumInventoryItems, out var policies)
                || !FixedString(retention, "backup_non_purge_warning_code", RuntimeBackupWarnings.RetentionBackupNotPurged))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            if (!TryExternalState(root.GetProperty("external_state"), out var externalState))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            var files = root.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1)
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
            var file = files.EnumerateArray().Single();
            if (!HasExactProperties(file, "path", "size", "sha256")
                || !FixedString(file, "path", "database.sqlite")
                || file.GetProperty("size").ValueKind != JsonValueKind.Number
                || !file.GetProperty("size").TryGetInt64(out var databaseSize)
                || !TrySha256(file.GetProperty("sha256"), out var databaseSha256)
                || !StringComparer.Ordinal.Equals(snapshotId, databaseSha256))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            if (!TryStringArray(root.GetProperty("warnings"), RuntimeBackupWarnings.All.Count, out var warnings)
                || !warnings.SequenceEqual(RuntimeBackupWarnings.All, StringComparer.Ordinal))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            var compatibility = root.GetProperty("compatibility");
            if (!HasExactProperties(compatibility, "minimum_reader_version", "maximum_reader_version", "migration_policy", "required_components")
                || compatibility.GetProperty("minimum_reader_version").ValueKind != JsonValueKind.Number
                || !compatibility.GetProperty("minimum_reader_version").TryGetInt32(out var minimumReaderVersion)
                || minimumReaderVersion != 1
                || compatibility.GetProperty("maximum_reader_version").ValueKind != JsonValueKind.Number
                || !compatibility.GetProperty("maximum_reader_version").TryGetInt32(out var maximumReaderVersion)
                || maximumReaderVersion != 1
                || !FixedString(compatibility, "migration_policy", "supported_older_only")
                || !TryIntMap(compatibility.GetProperty("required_components"), out var requiredComponents)
                || !MapEquals(componentVersions, requiredComponents))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

            var data = new RuntimeBackupManifestData(
                createdAt, sourceApplicationVersion, sourcePlatform, databaseSha256, databaseSize, sourceJournalMode,
                new RuntimeBackupBackupWindow(startedAt, completedAt, cursorsAtStart, cursorsAtEnd),
                componentVersions, rowCounts, projectionCursors,
                new RuntimeBackupRetentionSummary(storeKindCounts, stateCounts, tombstoneCount, earliestCapturedAt, latestCapturedAt, earliestExpiresAt, latestExpiresAt, policies),
                externalState);
            ValidateSemanticContract(data);
            var canonical = WriteManifest(data);
            if (!bytes.AsSpan().SequenceEqual(canonical)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestNotCanonical);
            return data;
        }
        catch (RuntimeBackupException) { throw; }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid); }
    }

    private static void ValidateSemanticContract(RuntimeBackupManifestData data)
    {
        if (!BoundedText(data.SourceApplicationVersion, MaximumStringTokenLength)
            || !BoundedText(data.SourcePlatform, MaximumStringTokenLength)
            || data.CreatedAt.Offset != TimeSpan.Zero
            || data.DatabaseSize is < 0 or > RuntimeBackupLimits.MaximumDatabaseBytes
            || !Sha256(data.DatabaseSha256)
            || data.BackupWindow is null
            || data.BackupWindow.StartedAt.Offset != TimeSpan.Zero
            || data.BackupWindow.CompletedAt.Offset != TimeSpan.Zero
            || data.BackupWindow.StartedAt > data.BackupWindow.CompletedAt
            || data.ComponentVersions.Count is 0 or > RuntimeBackupLimits.MaximumInventoryItems
            || data.RowCounts.Count > RuntimeBackupLimits.MaximumInventoryItems
            || data.ProjectionCursors.Count > RuntimeBackupLimits.MaximumInventoryItems
            || data.BackupWindow.ProjectionCursorsAtStart.Count > RuntimeBackupLimits.MaximumInventoryItems
            || data.BackupWindow.ProjectionCursorsAtEnd.Count > RuntimeBackupLimits.MaximumInventoryItems
            || data.Retention.Policies.Count > RuntimeBackupLimits.MaximumInventoryItems
            || data.ComponentVersions.Any(item => item.Value <= 0)
            || data.RowCounts.Any(item => item.Value < 0)
            || data.ProjectionCursors.Any(item => item.Value < 0)
            || data.BackupWindow.ProjectionCursorsAtStart.Any(item => item.Value < 0)
            || data.BackupWindow.ProjectionCursorsAtEnd.Any(item => item.Value < 0)
            || data.Retention.StoreKindCounts.Any(item => item.Value < 0)
            || data.Retention.StateCounts.Any(item => item.Value < 0)
            || data.Retention.TombstoneCount < 0
            || !NullableCanonicalUtcTimestamp(data.Retention.EarliestCapturedAt)
            || !NullableCanonicalUtcTimestamp(data.Retention.LatestCapturedAt)
            || !NullableCanonicalUtcTimestamp(data.Retention.EarliestExpiresAt)
            || !NullableCanonicalUtcTimestamp(data.Retention.LatestExpiresAt))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

        string[] journalModes = ["delete", "truncate", "persist", "memory", "wal", "off"];
        if (!journalModes.Contains(data.SourceJournalMode, StringComparer.Ordinal))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

        string[] storeKinds = ["analysis_run_raw", "analysis_sdk_directory", "raw_record", "sensitive_bundle", "session_event_content"];
        string[] retentionStates = ["deleted", "deleting", "deletion_failed", "deletion_queued", "expired_pending_deletion", "expiring", "retained_by_policy"];
        if (!data.Retention.StoreKindCounts.Keys.Order(StringComparer.Ordinal).SequenceEqual(storeKinds, StringComparer.Ordinal)
            || !data.Retention.StateCounts.Keys.Order(StringComparer.Ordinal).SequenceEqual(retentionStates, StringComparer.Ordinal)
            || data.Retention.Policies.Distinct(StringComparer.Ordinal).Count() != data.Retention.Policies.Count
            || !data.Retention.Policies.SequenceEqual(data.Retention.Policies.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || data.Retention.Policies.Any(value => !BoundedText(value, MaximumStringTokenLength))
            || data.ComponentVersions.Keys.Concat(data.RowCounts.Keys).Concat(data.ProjectionCursors.Keys)
                .Concat(data.BackupWindow.ProjectionCursorsAtStart.Keys).Concat(data.BackupWindow.ProjectionCursorsAtEnd.Keys)
                .Any(value => !BoundedMapKey(value))
            || !SameKeys(data.BackupWindow.ProjectionCursorsAtStart, data.ProjectionCursors)
            || !SameKeys(data.ProjectionCursors, data.BackupWindow.ProjectionCursorsAtEnd)
            || !CursorWindowIsMonotonic(data.BackupWindow, data.ProjectionCursors))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);

        RuntimeBackupExternalState[] expected =
        [
            new("ephemeral_runtime", data.ExternalState.ElementAtOrDefault(0)?.SourceState ?? "", false, "ephemeral", "restart_rematerializes"),
            new("setup_storage", data.ExternalState.ElementAtOrDefault(1)?.SourceState ?? "", false, "host_bound", "rerun_setup"),
            new("proposal_apply", data.ExternalState.ElementAtOrDefault(2)?.SourceState ?? "", false, "configuration_only", "reconfigure_apply_roots"),
            new("operator_backups", "not_inventoried", false, "operator_owned", "retain_or_delete_separately"),
        ];
        if (data.ExternalState.Count != expected.Length
            || !data.ExternalState.SequenceEqual(expected)
            || data.ExternalState[0].SourceState is not ("present" or "absent")
            || data.ExternalState[1].SourceState is not ("present" or "absent")
            || data.ExternalState[2].SourceState is not ("configured" or "empty" or "absent"))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
    }

    private const int MaximumStringTokenLength = 256;
    private const int MaximumMapKeyLength = 128;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value) { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value); }
    private static void WriteIntMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, int> values) { writer.WritePropertyName(name); writer.WriteStartObject(); foreach (var item in values.OrderBy(item => item.Key, StringComparer.Ordinal)) writer.WriteNumber(item.Key, item.Value); writer.WriteEndObject(); }
    private static void WriteLongMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, long> values) { writer.WritePropertyName(name); writer.WriteStartObject(); foreach (var item in values.OrderBy(item => item.Key, StringComparer.Ordinal)) writer.WriteNumber(item.Key, item.Value); writer.WriteEndObject(); }
    private static void WriteNullableLongMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, long?> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var item in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (item.Value is { } cursor) writer.WriteNumber(item.Key, cursor);
            else writer.WriteNull(item.Key);
        }
        writer.WriteEndObject();
    }

    private static bool HasExactProperties(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(property => property.Name).SequenceEqual(names, StringComparer.Ordinal);

    private static bool FixedString(JsonElement value, string name, string expected) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String
        && StringComparer.Ordinal.Equals(value.GetProperty(name).GetString(), expected);

    private static bool TryCanonicalUtcTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return TryBoundedString(value, 64, out var text)
            && CanonicalUtcTimestamp(text, out timestamp);
    }

    private static bool TryBoundedString(JsonElement value, int maximumLength, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } parsed || !BoundedText(parsed, maximumLength))
            return false;
        text = parsed;
        return true;
    }

    private static bool TryNullableCanonicalUtcTimestamp(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (!TryCanonicalUtcTimestamp(value, out _) || value.GetString() is not { } parsed) return false;
        text = parsed;
        return true;
    }

    private static bool TrySha256(JsonElement value, out string hash)
    {
        hash = string.Empty;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } parsed || !Sha256(parsed)) return false;
        hash = parsed;
        return true;
    }

    private static bool TryIntMap(JsonElement value, out Dictionary<string, int> result)
    {
        result = new(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems
                || !BoundedMapKey(property.Name)
                || property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out var number)
                || !result.TryAdd(property.Name, number)) return false;
        }
        return true;
    }

    private static bool TryLongMap(JsonElement value, out Dictionary<string, long> result)
    {
        result = new(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems
                || !BoundedMapKey(property.Name)
                || property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt64(out var number)
                || !result.TryAdd(property.Name, number)) return false;
        }
        return true;
    }

    private static bool TryNullableLongMap(JsonElement value, out Dictionary<string, long?> result)
    {
        result = new(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            long? number;
            if (property.Value.ValueKind == JsonValueKind.Null) number = null;
            else if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var parsed)) number = parsed;
            else return false;
            if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems
                || !BoundedMapKey(property.Name)
                || !result.TryAdd(property.Name, number)) return false;
        }
        return true;
    }

    private static bool TryStringArray(JsonElement value, int maximumItems, out List<string> result)
    {
        result = [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumItems) return false;
        foreach (var item in value.EnumerateArray())
        {
            if (!TryBoundedString(item, MaximumStringTokenLength, out var text)) return false;
            result.Add(text);
        }
        return true;
    }

    private static bool TryExternalState(JsonElement value, out List<RuntimeBackupExternalState> result)
    {
        result = [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) return false;
        foreach (var item in value.EnumerateArray())
        {
            if (!HasExactProperties(item, "kind", "source_state", "included", "consistency", "restore_action")
                || !TryBoundedString(item.GetProperty("kind"), MaximumStringTokenLength, out var kind)
                || !TryBoundedString(item.GetProperty("source_state"), MaximumStringTokenLength, out var sourceState)
                || item.GetProperty("included").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !TryBoundedString(item.GetProperty("consistency"), MaximumStringTokenLength, out var consistency)
                || !TryBoundedString(item.GetProperty("restore_action"), MaximumStringTokenLength, out var restoreAction)) return false;
            result.Add(new(kind, sourceState, item.GetProperty("included").GetBoolean(), consistency, restoreAction));
        }
        return true;
    }

    private static bool MapEquals(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);

    private static bool SameKeys(IReadOnlyDictionary<string, long?> left, IReadOnlyDictionary<string, long?> right) =>
        left.Count == right.Count && left.Keys.All(right.ContainsKey);

    private static bool CursorWindowIsMonotonic(RuntimeBackupBackupWindow window, IReadOnlyDictionary<string, long?> snapshot)
    {
        foreach (var key in snapshot.Keys)
        {
            var start = window.ProjectionCursorsAtStart[key];
            var captured = snapshot[key];
            var end = window.ProjectionCursorsAtEnd[key];
            if (start is { } startValue && captured is { } capturedValue && startValue > capturedValue) return false;
            if (captured is { } capturedCursor && end is { } endValue && capturedCursor > endValue) return false;
        }
        return true;
    }

    private static bool Sha256(string? value) => value is { Length: 64 }
        && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    private static bool BoundedMapKey(string? value) => BoundedText(value, MaximumMapKeyLength);
    private static bool NullableCanonicalUtcTimestamp(string? value) => value is null
        || BoundedText(value, 64) && CanonicalUtcTimestamp(value, out _);
    private static bool CanonicalUtcTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)
        && timestamp.Offset == TimeSpan.Zero
        && StringComparer.Ordinal.Equals(value, Timestamp(timestamp));
    private static bool BoundedText(string? value, int maximumLength) => value is { Length: > 0 }
        && value.Length <= maximumLength && !value.Any(char.IsControl);
}

internal sealed class RuntimeBackupException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

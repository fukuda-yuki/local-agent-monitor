using System.Globalization;
using System.Text.Json;

namespace CopilotAgentObservability.SanitizedExport;

internal static class SanitizedExportManifestValidator
{
    private static readonly string[] TopLevelProperties =
    [
        "schema_version", "bundle_schema_version", "bundle_profile", "created_at", "snapshot_id",
        "source_local_monitor_version", "source_agent_versions", "selection", "date_range", "source_labels",
        "record_counts", "known_missing_evidence", "capabilities", "completeness_distribution",
        "content_state_distribution", "retention_state_distribution", "processing_versions", "serialization",
        "compatibility", "repository_safe_validation", "files",
    ];

    internal static string? Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasExactProperties(root, TopLevelProperties)) return "manifest_invalid";
        if (!Fixed(root, "schema_version", SanitizedExportContractVersions.Manifest)
            || !Fixed(root, "bundle_schema_version", SanitizedExportContractVersions.BundleSchema)
            || !Fixed(root, "bundle_profile", SanitizedExportContractVersions.BundleProfile)) return "schema_unsupported";
        if (!CanonicalTimestamp(root.GetProperty("created_at"))
            || !NonemptyString(root.GetProperty("snapshot_id"))
            || !NonemptyString(root.GetProperty("source_local_monitor_version"))) return "manifest_invalid";
        if (!AgentVersions(root.GetProperty("source_agent_versions"))
            || !Selection(root.GetProperty("selection"))
            || !DateRange(root.GetProperty("date_range"))
            || !SourceLabels(root.GetProperty("source_labels"))
            || !CountMap(root.GetProperty("record_counts"))
            || !Unresolved(root.GetProperty("known_missing_evidence"))
            || !Capabilities(root.GetProperty("capabilities"))
            || !CountMap(root.GetProperty("completeness_distribution"))
            || !CountMap(root.GetProperty("content_state_distribution"))
            || !CountMap(root.GetProperty("retention_state_distribution"))
            || !StringMap(root.GetProperty("processing_versions"))) return "manifest_invalid";

        var serialization = root.GetProperty("serialization");
        var compatibility = root.GetProperty("compatibility");
        var validation = root.GetProperty("repository_safe_validation");
        if (!HasExactProperties(serialization, "canonical_json", "archive", "checksum")
            || !HasExactProperties(compatibility, "minimum_reader_major", "maximum_reader_major")
            || !HasExactProperties(validation, "profile", "result")) return "manifest_invalid";
        if (!Fixed(serialization, "canonical_json", SanitizedExportContractVersions.CanonicalJson)
            || !Fixed(serialization, "archive", SanitizedExportContractVersions.Archive)
            || !Fixed(serialization, "checksum", SanitizedExportContractVersions.Checksum)
            || !Fixed(compatibility, "minimum_reader_major", SanitizedExportContractVersions.CompatibilityMinimum)
            || !Fixed(compatibility, "maximum_reader_major", SanitizedExportContractVersions.CompatibilityMaximum)
            || !Fixed(validation, "profile", SanitizedExportContractVersions.Scanner)
            || !Fixed(validation, "result", "passed")) return "schema_unsupported";
        return Files(root.GetProperty("files")) ? null : "manifest_invalid";
    }

    private static bool AgentVersions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        var keys = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (!HasExactProperties(item, "source_surface", "version")
                || !NonemptyString(item.GetProperty("source_surface"))
                || !NonemptyString(item.GetProperty("version"))) return false;
            keys.Add($"{item.GetProperty("source_surface").GetString()}\0{item.GetProperty("version").GetString()}");
        }
        return IsOrdinal(keys);
    }

    private static bool Selection(JsonElement value)
    {
        if (!HasExactProperties(value, "session_ids", "trace_ids", "source_surfaces", "repository_names", "workspace_labels", "receipt_types", "start_inclusive", "end_exclusive")) return false;
        foreach (var name in new[] { "session_ids", "trace_ids", "source_surfaces", "repository_names", "workspace_labels", "receipt_types" })
            if (!SortedStrings(value.GetProperty(name))) return false;
        var start = value.GetProperty("start_inclusive");
        var end = value.GetProperty("end_exclusive");
        if (!NullableTimestamp(start) || !NullableTimestamp(end)) return false;
        return start.ValueKind == JsonValueKind.Null || end.ValueKind == JsonValueKind.Null
            || ParseTimestamp(start) < ParseTimestamp(end);
    }

    private static bool DateRange(JsonElement value)
    {
        if (!HasExactProperties(value, "start", "end")) return false;
        var start = value.GetProperty("start");
        var end = value.GetProperty("end");
        if (!NullableTimestamp(start) || !NullableTimestamp(end) || start.ValueKind != end.ValueKind) return false;
        return start.ValueKind == JsonValueKind.Null || ParseTimestamp(start) <= ParseTimestamp(end);
    }

    private static bool SourceLabels(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        var keys = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (!HasExactProperties(item, "repository_name", "workspace_label", "repo_snapshot")) return false;
            var parts = new[] { item.GetProperty("repository_name"), item.GetProperty("workspace_label"), item.GetProperty("repo_snapshot") };
            if (parts.Any(part => part.ValueKind != JsonValueKind.Null && !NonemptyString(part))) return false;
            keys.Add(string.Join('\0', parts.Select(part => part.ValueKind == JsonValueKind.Null ? string.Empty : part.GetString())));
        }
        return IsOrdinal(keys) && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count;
    }

    private static bool CountMap(JsonElement value) => StringMap(value, requireNonnegativeInteger: true);
    private static bool StringMap(JsonElement value) => StringMap(value, requireNonnegativeInteger: false);

    private static bool StringMap(JsonElement value, bool requireNonnegativeInteger)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (!IsOrdinal(names) || names.Any(string.IsNullOrWhiteSpace)) return false;
        return value.EnumerateObject().All(property => requireNonnegativeInteger
            ? property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var count) && count >= 0
            : NonemptyString(property.Value));
    }

    private static bool Unresolved(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        var keys = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (!HasExactProperties(item, "record_type", "record_id", "state")
                || !NonemptyString(item.GetProperty("record_type")) || !NonemptyString(item.GetProperty("record_id"))
                || item.GetProperty("state").GetString() is not ("missing" or "external")) return false;
            keys.Add($"{item.GetProperty("record_type").GetString()}\0{item.GetProperty("record_id").GetString()}");
        }
        return IsOrdinal(keys) && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count;
    }

    private static bool Capabilities(JsonElement value)
    {
        if (!HasExactProperties(value, "instruction_findings", "alert_receipts", "historical_instruction_analysis", "historical_efficiency_analysis", "alert_center")) return false;
        return value.EnumerateObject().All(property => property.Value.GetString() is "available" or "missing" or "unavailable");
    }

    private static bool Files(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > SanitizedExportLimits.MaximumArchiveEntries - 1) return false;
        foreach (var item in value.EnumerateArray())
        {
            if (!HasExactProperties(item, "path", "record_type", "record_id", "size", "sha256")
                || !NonemptyString(item.GetProperty("path")) || !NonemptyString(item.GetProperty("record_type"))
                || !NonemptyString(item.GetProperty("record_id"))
                || !item.GetProperty("size").TryGetInt64(out var size) || size < 1
                || item.GetProperty("sha256").GetString() is not { Length: 64 } hash
                || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) return false;
        }
        return true;
    }

    private static bool SortedStrings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        var values = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
        return values.All(item => !string.IsNullOrEmpty(item))
            && IsOrdinal(values!)
            && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    }

    private static bool HasExactProperties(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(property => property.Name).SequenceEqual(names, StringComparer.Ordinal);

    private static bool Fixed(JsonElement value, string name, string expected) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String && value.GetProperty(name).GetString() == expected;

    private static bool NonemptyString(JsonElement value) => value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString());
    private static bool NullableTimestamp(JsonElement value) => value.ValueKind == JsonValueKind.Null || CanonicalTimestamp(value);
    private static bool CanonicalTimestamp(JsonElement value) => value.ValueKind == JsonValueKind.String && ParseTimestamp(value) is not null;
    private static DateTimeOffset? ParseTimestamp(JsonElement value) =>
        DateTimeOffset.TryParseExact(value.GetString(), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    private static bool IsOrdinal(IEnumerable<string?> values)
    {
        string? previous = null;
        foreach (var value in values)
        {
            if (value is null || previous is not null && StringComparer.Ordinal.Compare(previous, value) > 0) return false;
            previous = value;
        }
        return true;
    }
}

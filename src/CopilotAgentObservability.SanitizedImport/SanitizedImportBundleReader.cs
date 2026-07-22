using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.SanitizedExport;

namespace CopilotAgentObservability.SanitizedImport;

internal sealed class SanitizedImportArchiveSnapshot
{
    private readonly byte[] bytes;

    private SanitizedImportArchiveSnapshot(byte[] bytes) => this.bytes = bytes;

    internal static SanitizedImportArchiveSnapshot Capture(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        return new(archiveBytes.ToArray());
    }

    internal SanitizedExportInspectionResult Inspect() => new SanitizedExportBundleInspector().Inspect(bytes);

    internal Stream OpenRead() => new MemoryStream(bytes, writable: false);
}

internal static class SanitizedImportBundleReader
{
    internal static SanitizedImportBundleReadResult Read(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        return archiveBytes.LongLength > SanitizedExportLimits.MaximumUncompressedBytes
            ? new(false, "bundle_too_large", null)
            : Read(SanitizedImportArchiveSnapshot.Capture(archiveBytes));
    }

    internal static SanitizedImportBundleReadResult Read(byte[] archiveBytes, Action afterInspection)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        return archiveBytes.LongLength > SanitizedExportLimits.MaximumUncompressedBytes
            ? new(false, "bundle_too_large", null)
            : Read(SanitizedImportArchiveSnapshot.Capture(archiveBytes), afterInspection);
    }

    internal static SanitizedImportBundleReadResult Read(SanitizedImportArchiveSnapshot archiveSnapshot) =>
        Read(archiveSnapshot, afterInspection: null);

    private static SanitizedImportBundleReadResult Read(
        SanitizedImportArchiveSnapshot archiveSnapshot,
        Action? afterInspection)
    {
        ArgumentNullException.ThrowIfNull(archiveSnapshot);
        try
        {
            var inspection = archiveSnapshot.Inspect();
            afterInspection?.Invoke();
            if (!inspection.Success) return new(false, inspection.ErrorCode, null);

            using var archive = new ZipArchive(archiveSnapshot.OpenRead(), ZipArchiveMode.Read);
            var manifestBytes = ReadEntry(archive.Entries[0]);
            using var document = JsonDocument.Parse(manifestBytes);
            var root = document.RootElement;
            var manifest = ParseManifest(root);
            var records = new List<SanitizedImportRecord>();
            foreach (var file in root.GetProperty("files").EnumerateArray())
            {
                var path = file.GetProperty("path").GetString()!;
                var recordType = file.GetProperty("record_type").GetString()!;
                var recordId = file.GetProperty("record_id").GetString()!;
                var canonicalBytes = ReadEntry(archive.GetEntry(path)!);
                records.Add(new(
                    path,
                    recordType,
                    recordId,
                    SanitizedImportIdentity.Hash("sanitized-import-record.v1", recordType, recordId),
                    file.GetProperty("sha256").GetString()!,
                    canonicalBytes));
            }

            var graph = SanitizedImportGraphProjector.Project(records, manifest.KnownMissingEvidence);
            var declarations = BuildDeclarations(graph.Nodes, manifest.KnownMissingEvidence);
            return new(true, null, new(
                inspection.ArchiveSha256!,
                inspection.TotalUncompressedBytes,
                manifest,
                records,
                graph.Nodes,
                declarations,
                graph.Edges));
        }
        catch (SanitizedImportGraphLimitException)
        {
            return new(false, "graph_limit_exceeded", null);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException
            or FormatException or OverflowException or AlertReceiptConsumerException
            or InstructionFindingHandoffConsumerValidationException)
        {
            return new(false, "archive_invalid", null);
        }
    }

    private static IReadOnlyList<SanitizedImportGraphDeclaration> BuildDeclarations(
        IReadOnlyList<SanitizedImportGraphNode> nodes,
        IReadOnlyList<SanitizedImportUnresolved> knownMissing)
    {
        var nodesById = nodes.ToDictionary(node => node.LocalNodeId, StringComparer.Ordinal);
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in knownMissing)
        {
            var nodeId = SanitizedImportIdentity.Hash("sanitized-import-node.v1", item.NodeKind, item.SourceId);
            if (!nodesById.TryGetValue(nodeId, out var node) || node.State == "defined")
                throw new InvalidDataException();
            if (declarations.TryGetValue(nodeId, out var state) && state != item.State)
                throw new InvalidDataException();
            declarations[nodeId] = item.State;
        }
        return declarations
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new SanitizedImportGraphDeclaration(item.Key, item.Value))
            .ToArray();
    }

    private static SanitizedImportManifest ParseManifest(JsonElement root)
    {
        var capabilities = root.GetProperty("capabilities");
        return new(
            root.GetProperty("schema_version").GetString()!,
            root.GetProperty("bundle_schema_version").GetString()!,
            root.GetProperty("bundle_profile").GetString()!,
            Timestamp(root.GetProperty("created_at")),
            root.GetProperty("snapshot_id").GetString()!,
            root.GetProperty("source_local_monitor_version").GetString()!,
            root.GetProperty("source_agent_versions").EnumerateArray()
                .Select(item => new SanitizedImportSourceAgentVersion(
                    item.GetProperty("source_surface").GetString()!,
                    item.GetProperty("version").GetString()!)).ToArray(),
            ParseSelection(root.GetProperty("selection")),
            ParseDateRange(root.GetProperty("date_range")),
            root.GetProperty("source_labels").EnumerateArray()
                .Select(item => new SanitizedImportSourceLabel(
                    NullableString(item.GetProperty("repository_name")),
                    NullableString(item.GetProperty("workspace_label")),
                    NullableString(item.GetProperty("repo_snapshot")))).ToArray(),
            IntMap(root.GetProperty("record_counts")),
            root.GetProperty("known_missing_evidence").EnumerateArray()
                .Select(item => new SanitizedImportUnresolved(
                    $"record:{item.GetProperty("record_type").GetString()!}",
                    item.GetProperty("record_id").GetString()!,
                    item.GetProperty("state").GetString()!)).ToArray(),
            new(
                capabilities.GetProperty("instruction_findings").GetString()!,
                capabilities.GetProperty("alert_receipts").GetString()!,
                capabilities.GetProperty("historical_instruction_analysis").GetString()!,
                capabilities.GetProperty("historical_efficiency_analysis").GetString()!,
                capabilities.GetProperty("alert_center").GetString()!),
            IntMap(root.GetProperty("completeness_distribution")),
            IntMap(root.GetProperty("content_state_distribution")),
            IntMap(root.GetProperty("retention_state_distribution")),
            StringMap(root.GetProperty("processing_versions")));
    }

    private static SanitizedImportSelection ParseSelection(JsonElement value) => new(
        Strings(value.GetProperty("session_ids")),
        Strings(value.GetProperty("trace_ids")),
        Strings(value.GetProperty("source_surfaces")),
        Strings(value.GetProperty("repository_names")),
        Strings(value.GetProperty("workspace_labels")),
        Strings(value.GetProperty("receipt_types")),
        NullableTimestamp(value.GetProperty("start_inclusive")),
        NullableTimestamp(value.GetProperty("end_exclusive")));

    private static SanitizedImportDateRange ParseDateRange(JsonElement value) => new(
        NullableTimestamp(value.GetProperty("start")),
        NullableTimestamp(value.GetProperty("end")));

    private static IReadOnlyList<string> Strings(JsonElement value) =>
        value.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static IReadOnlyDictionary<string, int> IntMap(JsonElement value) =>
        value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetInt32(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> StringMap(JsonElement value) =>
        value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);

    private static string? NullableString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    private static DateTimeOffset? NullableTimestamp(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Timestamp(value);
    private static DateTimeOffset Timestamp(JsonElement value) => DateTimeOffset.ParseExact(
        value.GetString()!, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new InvalidDataException();
            offset += read;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        return bytes;
    }
}

internal static class SanitizedImportIdentity
{
    internal static string Hash(string domain, params string[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var part in parts) Append(hash, part);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

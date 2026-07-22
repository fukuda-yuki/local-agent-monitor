using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.SanitizedExport;

internal sealed class SanitizedExportService
{
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public SanitizedExportPreview Preview(SanitizedExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationError = ValidateRequest(request);
        if (validationError is not null) return FailurePreview(request, validationError);

        var selected = Select(request.Snapshot.Records, request.Selection);
        var byIdentity = request.Snapshot.Records.ToDictionary(record => (record.RecordType, record.RecordId));
        var included = new List<SanitizedExportRecord>();
        var includedIdentities = new HashSet<(string, string)>();
        var unresolved = new Dictionary<(string, string), string>();
        var queue = new Queue<SanitizedExportRecord>(selected);
        while (queue.TryDequeue(out var record))
        {
            if (!includedIdentities.Add((record.RecordType, record.RecordId))) continue;
            included.Add(record);
            foreach (var dependency in record.Dependencies.OrderBy(item => item.RecordType, StringComparer.Ordinal).ThenBy(item => item.RecordId, StringComparer.Ordinal))
            {
                if (dependency.Disposition == SanitizedExportDependencyDisposition.External)
                {
                    var identity = (dependency.RecordType, dependency.RecordId);
                    if (!unresolved.ContainsKey(identity)) unresolved[identity] = "external";
                }
                else if (byIdentity.TryGetValue((dependency.RecordType, dependency.RecordId), out var resolved))
                {
                    queue.Enqueue(resolved);
                }
                else
                {
                    var identity = (dependency.RecordType, dependency.RecordId);
                    var state = dependency.Disposition == SanitizedExportDependencyDisposition.External ? "external" : "missing";
                    if (!unresolved.TryGetValue(identity, out var current) || current == "external" && state == "missing")
                        unresolved[identity] = state;
                }
            }
        }

        if (included.Count + 1 > SanitizedExportLimits.MaximumArchiveEntries)
            return FailurePreview(request, "entry_limit_exceeded");
        if (included.Select(record => (long)record.CanonicalBytes.Length).Sum() >= SanitizedExportLimits.MaximumUncompressedBytes)
            return FailurePreview(request, "uncompressed_size_limit_exceeded");

        var duplicatePath = included.GroupBy(record => record.EntryPath, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicatePath is not null) return FailurePreview(request, "duplicate_entry");

        foreach (var record in included)
        {
            var scanError = SanitizedExportScanner.Scan(record, request.ForbiddenMarkers);
            if (scanError is not null) return FailurePreview(request, scanError);
        }

        var entries = included.OrderBy(record => record.EntryPath, StringComparer.Ordinal)
            .Select(record => new SanitizedExportPreviewEntry(
                record.EntryPath,
                record.RecordType,
                record.RecordId,
                record.CanonicalBytes.LongLength,
                Hash(record.CanonicalBytes))).ToArray();
        var missing = unresolved.OrderBy(item => item.Key.Item1, StringComparer.Ordinal).ThenBy(item => item.Key.Item2, StringComparer.Ordinal)
            .Select(item => new SanitizedExportUnresolvedDependency(item.Key.Item1, item.Key.Item2, item.Value)).ToArray();
        var bundleCapabilities = new SanitizedExportCapabilityStates(
            entries.Any(entry => entry.RecordType == "instruction_finding_handoff") ? "available" : "missing",
            entries.Any(entry => entry.RecordType == "alert_receipt") ? "available" : "missing",
            "unavailable", "unavailable", "unavailable");
        var preview = new SanitizedExportPreview(true, null, entries, missing, entries.Sum(entry => entry.Size), bundleCapabilities, SanitizedExportContractVersions.Scanner);
        var manifest = SanitizedExportManifestWriter.Write(request, preview);
        var manifestError = ValidateManifestBytes(manifest);
        if (manifestError is not null) return FailurePreview(request, manifestError);
        var manifestScan = SanitizedExportScanner.ScanCanonicalJson(manifest);
        if (manifestScan is not null) return FailurePreview(request, manifestScan);
        var totalUncompressedBytes = preview.EstimatedUncompressedBytes + manifest.LongLength;
        if (totalUncompressedBytes > SanitizedExportLimits.MaximumUncompressedBytes)
            return FailurePreview(request, "uncompressed_size_limit_exceeded");
        return preview with { EstimatedUncompressedBytes = totalUncompressedBytes };
    }

    public SanitizedExportResult Create(SanitizedExportRequest request)
    {
        var preview = Preview(request);
        if (!preview.Success) return new(false, preview.ErrorCode, preview, null, null, null);

        var recordsByIdentity = request.Snapshot.Records.ToDictionary(record => (record.RecordType, record.RecordId));
        var manifest = SanitizedExportManifestWriter.Write(request, preview);
        if (preview.EstimatedUncompressedBytes > SanitizedExportLimits.MaximumUncompressedBytes)
            return new(false, "uncompressed_size_limit_exceeded", FailurePreview(request, "uncompressed_size_limit_exceeded"), null, null, null);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", manifest);
            foreach (var entry in preview.Entries.OrderBy(item => item.Path, StringComparer.Ordinal))
                WriteEntry(archive, entry.Path, recordsByIdentity[(entry.RecordType, entry.RecordId)].CanonicalBytes);
        }
        var bytes = output.ToArray();
        if (bytes.LongLength > SanitizedExportLimits.MaximumUncompressedBytes)
            return new(false, "bundle_too_large", FailurePreview(request, "bundle_too_large"), null, null, null);
        var inspection = Inspect(bytes);
        if (!inspection.Success)
            return new(false, inspection.ErrorCode, preview, null, null, null);
        return new(true, null, preview, manifest, bytes, Hash(bytes));
    }

    public SanitizedExportResult CreateAndPublish(SanitizedExportRequest request, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return new(false, "publish_failed", FailurePreview(request, "publish_failed"), null, null, null);
        var result = Create(request);
        if (!result.Success) return result;

        string? temporaryPath = null;
        try
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory)) return PublicationFailure(result, "publish_failed");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
            if (File.Exists(fullPath)) return PublicationFailure(result, "output_exists");
            File.WriteAllBytes(temporaryPath, result.ArchiveBytes!);
            File.Move(temporaryPath, fullPath, overwrite: false);
            return result with { PublishedFileName = Path.GetFileName(fullPath) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return PublicationFailure(result, "publish_failed");
        }
        finally
        {
            if (temporaryPath is not null) TryDelete(temporaryPath);
        }
    }

    public SanitizedExportInspectionResult Inspect(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.LongLength > SanitizedExportLimits.MaximumUncompressedBytes)
            return InspectionFailure("bundle_too_large");
        try
        {
            using var archive = new ZipArchive(new MemoryStream(archiveBytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count is 0 or > SanitizedExportLimits.MaximumArchiveEntries) return InspectionFailure("entry_limit_exceeded");
            var storageError = ValidateStoredArchive(archiveBytes, archive.Entries.Count);
            if (storageError is not null) return InspectionFailure(storageError);
            long totalUncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0 || entry.Length > SanitizedExportLimits.MaximumUncompressedBytes - totalUncompressedBytes)
                    return InspectionFailure("uncompressed_size_limit_exceeded");
                totalUncompressedBytes += entry.Length;
            }
            if (archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Any(group => group.Count() > 1)) return InspectionFailure("duplicate_entry");
            if (archive.Entries.Any(entry => entry.CompressedLength != entry.Length)) return InspectionFailure("compression_not_allowed");
            if (archive.Entries.Any(entry => entry.LastWriteTime.DateTime != new DateTime(1980, 1, 1))) return InspectionFailure("archive_timestamp_invalid");
            if (archive.Entries.Any(entry => entry.ExternalAttributes != 0)) return InspectionFailure("archive_attributes_invalid");
            var manifestEntry = archive.Entries.SingleOrDefault(entry => entry.FullName == "manifest.json");
            if (manifestEntry is null || archive.Entries[0] != manifestEntry) return InspectionFailure("manifest_missing");
            var manifestBytes = ReadEntry(manifestEntry);
            if (manifestBytes is null) return InspectionFailure("archive_invalid");
            var manifestScan = SanitizedExportScanner.ScanCanonicalJson(manifestBytes);
            if (manifestScan is not null) return InspectionFailure(manifestScan);
            using var manifest = JsonDocument.Parse(manifestBytes);
            var root = manifest.RootElement;
            var manifestError = SanitizedExportManifestValidator.Validate(root);
            if (manifestError is not null) return InspectionFailure(manifestError);
            if (!manifestBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(root)))
                return InspectionFailure("manifest_not_canonical");
            var files = root.GetProperty("files").EnumerateArray().ToArray();
            if (files.Any(file => file.GetProperty("path").GetString() == "manifest.json") || files.Length != archive.Entries.Count - 1)
                return InspectionFailure("file_inventory_invalid");
            var manifestPaths = files.Select(file => file.GetProperty("path").GetString()).ToArray();
            var archivePaths = archive.Entries.Skip(1).Select(entry => entry.FullName).ToArray();
            if (!manifestPaths.SequenceEqual(archivePaths, StringComparer.Ordinal)
                || !archivePaths.SequenceEqual(archivePaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return InspectionFailure("entry_order_invalid");
            if (files.GroupBy(file => (file.GetProperty("record_type").GetString(), file.GetProperty("record_id").GetString())).Any(group => group.Count() != 1))
                return InspectionFailure("file_inventory_invalid");
            var actualCounts = files.GroupBy(file => file.GetProperty("record_type").GetString()!, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var declaredCounts = root.GetProperty("record_counts").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetInt32(), StringComparer.Ordinal);
            if (!actualCounts.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(declaredCounts.OrderBy(item => item.Key, StringComparer.Ordinal)))
                return InspectionFailure("file_inventory_invalid");
            var capabilities = root.GetProperty("capabilities");
            if (capabilities.GetProperty("instruction_findings").GetString()
                    != (actualCounts.ContainsKey("instruction_finding_handoff") ? "available" : "missing")
                || capabilities.GetProperty("alert_receipts").GetString()
                    != (actualCounts.ContainsKey("alert_receipt") ? "available" : "missing"))
                return InspectionFailure("file_inventory_invalid");
            foreach (var file in files)
            {
                var path = file.GetProperty("path").GetString()!;
                var entry = archive.GetEntry(path);
                if (entry is null || entry.Length != file.GetProperty("size").GetInt64()) return InspectionFailure("file_inventory_invalid");
                var bytes = ReadEntry(entry);
                if (bytes is null) return InspectionFailure("archive_invalid");
                if (Hash(bytes) != file.GetProperty("sha256").GetString()) return InspectionFailure("checksum_mismatch");
                var record = new SanitizedExportRecord(path, file.GetProperty("record_type").GetString()!, file.GetProperty("record_id").GetString()!, null, null, null, null, null, null, DateTimeOffset.UnixEpoch, bytes, []);
                var scanError = SanitizedExportScanner.Scan(record, []);
                if (scanError is not null) return InspectionFailure(scanError);
                var producerError = SanitizedExportProducerValidator.Validate(record, requireEnvelope: false);
                if (producerError is not null) return InspectionFailure(producerError);
            }
            if (totalUncompressedBytes != manifestBytes.LongLength + files.Sum(file => file.GetProperty("size").GetInt64()))
                return InspectionFailure("file_inventory_invalid");
            return new(
                true,
                null,
                Hash(archiveBytes),
                SanitizedExportContractVersions.Manifest,
                SanitizedExportContractVersions.BundleSchema,
                SanitizedExportContractVersions.BundleProfile,
                files.Length,
                totalUncompressedBytes);
        }
        catch (InvalidDataException) { return InspectionFailure("archive_invalid"); }
        catch (JsonException) { return InspectionFailure("manifest_invalid"); }
        catch (InvalidOperationException) { return InspectionFailure("manifest_invalid"); }
        catch (KeyNotFoundException) { return InspectionFailure("manifest_invalid"); }
        catch (ArgumentOutOfRangeException) { return InspectionFailure("archive_invalid"); }
        catch (OverflowException) { return InspectionFailure("archive_invalid"); }
    }

    private static string? ValidateRequest(SanitizedExportRequest request)
    {
        if (request.CreatedAt.Offset != TimeSpan.Zero || InvalidIdentifier(request.Snapshot.SnapshotId)
            || request.Snapshot.Records.Count > SanitizedExportLimits.MaximumRecords
            || request.Snapshot.AgentVersions.Count > SanitizedExportLimits.MaximumVersions
            || request.Snapshot.ProcessingVersions?.Count > SanitizedExportLimits.MaximumVersions
            || request.ForbiddenMarkers.Count > SanitizedExportLimits.MaximumForbiddenMarkers
            || request.ForbiddenMarkers.Any(marker => string.IsNullOrEmpty(marker) || marker.Length > SanitizedExportLimits.MaximumMarkerLength)
            || InvalidBoundedText(request.Snapshot.LocalMonitorVersion)
            || request.Snapshot.AgentVersions.Any(item => InvalidBoundedText(item.SourceSurface) || InvalidBoundedText(item.Version))
            || request.Snapshot.AgentVersions.GroupBy(item => (item.SourceSurface, item.Version)).Any(group => group.Count() > 1)
            || request.Snapshot.ProcessingVersions?.Any(item => InvalidBoundedText(item.Key) || InvalidBoundedText(item.Value)) == true)
            return "invalid_request";
        if (request.Selection.StartInclusive is { } start && start.Offset != TimeSpan.Zero
            || request.Selection.EndExclusive is { } end && end.Offset != TimeSpan.Zero
            || request.Selection.StartInclusive >= request.Selection.EndExclusive) return "invalid_selection";
        if (request.Snapshot.Records.GroupBy(record => (record.RecordType, record.RecordId)).Any(group => group.Count() > 1))
            return "duplicate_record_identity";
        if (request.Snapshot.Records.GroupBy(record => record.EntryPath, StringComparer.Ordinal).Any(group => group.Count() > 1))
            return "duplicate_entry";
        if (request.Snapshot.Records.Any(record => record.Dependencies.Count > SanitizedExportLimits.MaximumDependenciesPerRecord)
            || SelectionLists(request.Selection).Any(list => list is { Count: > SanitizedExportLimits.MaximumListValues })) return "invalid_request";
        if (request.Snapshot.Records.SelectMany(record => record.Dependencies).Any(dependency => !SanitizedExportProducerValidator.AllowedType(dependency.RecordType)))
            return "unsupported_record_type";
        if (request.Selection.ReceiptTypes?.Any(type => !SanitizedExportProducerValidator.AllowedType(type)) == true)
            return "unsupported_record_type";
        var capabilities = request.Snapshot.Capabilities;
        if (!ValidCapability(capabilities.InstructionFindings)
            || !ValidCapability(capabilities.AlertReceipts)
            || !ValidCapability(capabilities.HistoricalInstructionAnalysis)
            || !ValidCapability(capabilities.HistoricalEfficiencyAnalysis)
            || !ValidCapability(capabilities.AlertCenter)) return "invalid_capability_state";
        if (capabilities.HistoricalInstructionAnalysis != "unavailable" || capabilities.HistoricalEfficiencyAnalysis != "unavailable" || capabilities.AlertCenter != "unavailable")
            return "invalid_capability_state";
        var hasInstruction = request.Snapshot.Records.Any(record => record.RecordType == "instruction_finding_handoff");
        var hasAlert = request.Snapshot.Records.Any(record => record.RecordType == "alert_receipt");
        if ((capabilities.InstructionFindings == "available") != hasInstruction || (capabilities.AlertReceipts == "available") != hasAlert)
            return "invalid_capability_state";
        if (request.Snapshot.Records.Any(record => record.EntryPath.Length is 0 or > 320 || record.EntryPath.Any(char.IsControl)
            || InvalidBoundedText(record.RecordType) || InvalidBoundedText(record.RecordId)
            || InvalidNullableText(record.SessionId) || InvalidNullableText(record.TraceId) || InvalidNullableText(record.SourceSurface)
            || InvalidNullableText(record.RepositoryName) || InvalidNullableText(record.WorkspaceLabel) || InvalidNullableText(record.RepoSnapshot)
            || InvalidBoundedText(record.Completeness) || InvalidBoundedText(record.ContentState) || InvalidBoundedText(record.RetentionState)
            || record.Dependencies.Any(dependency => InvalidBoundedText(dependency.RecordType) || InvalidBoundedText(dependency.RecordId)))
            || SelectionValues(request.Selection).Any(InvalidNullableText)
            || SelectionLists(request.Selection).Any(list => list?.Distinct(StringComparer.Ordinal).Count() != list?.Count))
            return "invalid_request";
        var metadata = new List<string?>
        {
            request.Snapshot.SnapshotId,
            request.Snapshot.LocalMonitorVersion,
        };
        metadata.AddRange(request.Snapshot.AgentVersions.SelectMany(item => new[] { item.SourceSurface, item.Version }));
        metadata.AddRange(request.Snapshot.ProcessingVersions?.SelectMany(item => new[] { item.Key, item.Value }) ?? []);
        metadata.AddRange(request.Snapshot.Records.SelectMany(record => new[]
        {
            record.EntryPath, record.RecordType, record.RecordId, record.SessionId, record.TraceId, record.SourceSurface,
            record.RepositoryName, record.WorkspaceLabel, record.RepoSnapshot, record.Completeness, record.ContentState, record.RetentionState,
        }));
        metadata.AddRange(request.Snapshot.Records.SelectMany(record => record.Dependencies).SelectMany(item => new[] { item.RecordType, item.RecordId }));
        metadata.AddRange(SelectionValues(request.Selection));
        var metadataError = SanitizedExportScanner.ScanMetadata(metadata, request.ForbiddenMarkers);
        if (metadataError is not null) return metadataError;
        var manifestIdentifiers = request.Snapshot.AgentVersions.SelectMany(item => new[] { item.SourceSurface, item.Version })
            .Concat(request.Snapshot.ProcessingVersions?.SelectMany(item => new[] { item.Key, item.Value }) ?? [])
            .Concat(request.Snapshot.Records.SelectMany(record => new[]
            {
                record.RecordType, record.RecordId, record.SessionId, record.TraceId, record.SourceSurface,
                record.RepositoryName, record.WorkspaceLabel, record.RepoSnapshot, record.Completeness, record.ContentState, record.RetentionState,
            }))
            .Concat(request.Snapshot.Records.SelectMany(record => record.Dependencies).SelectMany(item => new[] { item.RecordType, item.RecordId }))
            .Concat(SelectionValues(request.Selection));
        if (InvalidIdentifier(request.Snapshot.LocalMonitorVersion)
            || manifestIdentifiers.Any(value => value is not null && InvalidIdentifier(value))) return "invalid_request";
        foreach (var record in request.Snapshot.Records)
        {
            var scanError = SanitizedExportScanner.Scan(record, request.ForbiddenMarkers);
            if (scanError is not null && scanError != "unexpected_entry") return scanError;
            var producerError = SanitizedExportProducerValidator.Validate(record);
            if (producerError is not null) return producerError;
        }
        return null;
    }

    private static bool ValidCapability(string value) => value is "available" or "missing" or "unavailable";
    private static bool InvalidBoundedText(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > SanitizedExportLimits.MaximumIdentifierLength || value.Any(char.IsControl);
    private static bool InvalidNullableText(string? value) => value is not null && InvalidBoundedText(value);
    private static bool InvalidIdentifier(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > SanitizedExportLimits.MaximumIdentifierLength || value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#');
    private static IEnumerable<IReadOnlyList<string>?> SelectionLists(SanitizedExportSelection selection) =>
        [selection.SessionIds, selection.TraceIds, selection.SourceSurfaces, selection.RepositoryNames, selection.WorkspaceLabels, selection.ReceiptTypes];

    private static IEnumerable<string?> SelectionValues(SanitizedExportSelection selection) =>
        (selection.SessionIds ?? []).Cast<string?>()
            .Concat(selection.TraceIds ?? []).Concat(selection.SourceSurfaces ?? []).Concat(selection.RepositoryNames ?? [])
            .Concat(selection.WorkspaceLabels ?? []).Concat(selection.ReceiptTypes ?? []);

    private static IReadOnlyList<SanitizedExportRecord> Select(IReadOnlyList<SanitizedExportRecord> records, SanitizedExportSelection selection)
    {
        var hasIds = Has(selection.SessionIds) || Has(selection.TraceIds);
        return records.Where(record =>
                (!hasIds || Contains(selection.SessionIds, record.SessionId) || Contains(selection.TraceIds, record.TraceId))
                && (!Has(selection.SourceSurfaces) || Contains(selection.SourceSurfaces, record.SourceSurface))
                && (!Has(selection.RepositoryNames) || Contains(selection.RepositoryNames, record.RepositoryName))
                && (!Has(selection.WorkspaceLabels) || Contains(selection.WorkspaceLabels, record.WorkspaceLabel))
                && (selection.StartInclusive is null || record.ObservedAt >= selection.StartInclusive)
                && (selection.EndExclusive is null || record.ObservedAt < selection.EndExclusive)
                && (!Has(selection.ReceiptTypes) || Contains(selection.ReceiptTypes, record.RecordType)))
            .OrderBy(record => record.ObservedAt).ThenBy(record => record.RecordType, StringComparer.Ordinal).ThenBy(record => record.RecordId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Has(IReadOnlyList<string>? values) => values is { Count: > 0 };
    private static bool Contains(IReadOnlyList<string>? values, string? value) => value is not null && values?.Contains(value, StringComparer.Ordinal) == true;

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = ArchiveTimestamp;
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[]? ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length < 0 || entry.Length > SanitizedExportLimits.MaximumUncompressedBytes) return null;
        using var stream = entry.Open();
        var output = new byte[(int)entry.Length];
        var offset = 0;
        while (offset < output.Length)
        {
            var read = stream.Read(output, offset, output.Length - offset);
            if (read == 0) return null;
            offset += read;
        }
        return stream.ReadByte() == -1 ? output : null;
    }

    private static string? ValidateStoredArchive(byte[] bytes, int expectedEntries)
    {
        const uint endSignature = 0x06054b50;
        const uint centralSignature = 0x02014b50;
        const uint localSignature = 0x04034b50;
        var minimumOffset = Math.Max(0, bytes.Length - 65_557);
        var endOffset = -1;
        for (var index = bytes.Length - 22; index >= minimumOffset; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == endSignature)
            {
                endOffset = index;
                break;
            }
        }
        if (endOffset < 0) return "archive_invalid";

        var end = bytes.AsSpan(endOffset);
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(end[20..22]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[8..10]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..12]);
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..16]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..20]);
        if (commentLength != 0 || endOffset + 22 != bytes.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(end[4..6]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(end[6..8]) != 0
            || entriesOnDisk != expectedEntries || totalEntries != expectedEntries
            || centralOffset > int.MaxValue || centralSize > int.MaxValue
            || (long)centralOffset + centralSize != endOffset) return "archive_invalid";

        var cursor = (int)centralOffset;
        var expectedLocalOffset = 0;
        for (var entryIndex = 0; entryIndex < expectedEntries; entryIndex++)
        {
            if (cursor < 0 || cursor > endOffset - 46
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)) != centralSignature)
                return "archive_invalid";
            var central = bytes.AsSpan(cursor, 46);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(central[42..46]);
            if (localOffset > int.MaxValue || localOffset != expectedLocalOffset || localOffset > bytes.Length - 30
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)localOffset, 4)) != localSignature)
                return "archive_invalid";
            var centralMethod = BinaryPrimitives.ReadUInt16LittleEndian(central[10..12]);
            var localMethod = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 8, 2));
            if (centralMethod != 0 || localMethod != 0) return "compression_not_allowed";
            if (BinaryPrimitives.ReadUInt16LittleEndian(central[4..6]) != 20
                || BinaryPrimitives.ReadUInt16LittleEndian(central[6..8]) != 20
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 4, 2)) != 20)
                return "archive_invalid";
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(central[28..30]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(central[30..32]);
            var entryCommentLength = BinaryPrimitives.ReadUInt16LittleEndian(central[32..34]);
            var local = bytes.AsSpan((int)localOffset, 30);
            var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(local[26..28]);
            var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(local[28..30]);
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(local[18..22]);
            if (extraLength != 0 || entryCommentLength != 0 || localExtraLength != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(central[34..36]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(central[36..38]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(central[8..10]) is var centralFlags && (centralFlags & ~0x0800) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(local[6..8]) != centralFlags
                || !central[12..28].SequenceEqual(local[10..26])
                || localNameLength != nameLength) return "archive_invalid";
            var centralName = bytes.AsSpan(cursor + 46, nameLength);
            var localName = bytes.AsSpan((int)localOffset + 30, localNameLength);
            if (!centralName.SequenceEqual(localName)) return "archive_invalid";
            var nextLocal = (long)localOffset + 30 + localNameLength + compressedSize;
            if (nextLocal > centralOffset) return "archive_invalid";
            expectedLocalOffset = (int)nextLocal;
            var next = (long)cursor + 46 + nameLength + extraLength + entryCommentLength;
            if (next > endOffset) return "archive_invalid";
            cursor = (int)next;
        }
        return cursor == endOffset && expectedLocalOffset == centralOffset ? null : "archive_invalid";
    }

    private static string? ValidateManifestBytes(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var error = SanitizedExportManifestValidator.Validate(document.RootElement);
            if (error is not null) return error;
            return bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(document.RootElement)) ? null : "manifest_not_canonical";
        }
        catch (JsonException) { return "manifest_invalid"; }
    }

    private static SanitizedExportResult PublicationFailure(SanitizedExportResult result, string code) =>
        result with { Success = false, ErrorCode = code, ManifestBytes = null, ArchiveBytes = null, ArchiveSha256 = null };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
        }
    }

    private static SanitizedExportInspectionResult InspectionFailure(string code) => new(false, code, null);

    private static SanitizedExportPreview FailurePreview(SanitizedExportRequest request, string code) =>
        new(false, code, [], [], 0, request.Snapshot.Capabilities, SanitizedExportContractVersions.Scanner);
}

internal static class SanitizedExportManifestWriter
{
    internal static byte[] Write(SanitizedExportRequest request, SanitizedExportPreview preview)
    {
        var selectedRecords = preview.Entries
            .Select(entry => request.Snapshot.Records.Single(record => record.RecordType == entry.RecordType && record.RecordId == entry.RecordId))
            .ToArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SanitizedExportContractVersions.Manifest);
            writer.WriteString("bundle_schema_version", SanitizedExportContractVersions.BundleSchema);
            writer.WriteString("bundle_profile", SanitizedExportContractVersions.BundleProfile);
            writer.WriteString("created_at", Timestamp(request.CreatedAt));
            writer.WriteString("snapshot_id", request.Snapshot.SnapshotId);
            writer.WriteString("source_local_monitor_version", request.Snapshot.LocalMonitorVersion);
            writer.WritePropertyName("source_agent_versions");
            writer.WriteStartArray();
            foreach (var version in request.Snapshot.AgentVersions.OrderBy(item => item.SourceSurface, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("source_surface", version.SourceSurface); writer.WriteString("version", version.Version); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteSelection(writer, request.Selection);
            writer.WritePropertyName("date_range"); writer.WriteStartObject();
            if (selectedRecords.Length == 0)
            {
                writer.WriteNull("start"); writer.WriteNull("end");
            }
            else
            {
                writer.WriteString("start", Timestamp(selectedRecords.Min(record => record.ObservedAt)));
                writer.WriteString("end", Timestamp(selectedRecords.Max(record => record.ObservedAt)));
            }
            writer.WriteEndObject();
            writer.WritePropertyName("source_labels"); writer.WriteStartArray();
            foreach (var label in selectedRecords
                .Select(record => (record.RepositoryName, record.WorkspaceLabel, record.RepoSnapshot))
                .Distinct()
                .OrderBy(item => item.RepositoryName, StringComparer.Ordinal)
                .ThenBy(item => item.WorkspaceLabel, StringComparer.Ordinal)
                .ThenBy(item => item.RepoSnapshot, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); NullableString(writer, "repository_name", label.RepositoryName);
                NullableString(writer, "workspace_label", label.WorkspaceLabel); NullableString(writer, "repo_snapshot", label.RepoSnapshot); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("record_counts"); writer.WriteStartObject();
            foreach (var group in preview.Entries.GroupBy(item => item.RecordType, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)) writer.WriteNumber(group.Key, group.Count());
            writer.WriteEndObject();
            writer.WritePropertyName("known_missing_evidence"); writer.WriteStartArray();
            foreach (var missing in preview.UnresolvedDependencies)
            {
                writer.WriteStartObject(); writer.WriteString("record_type", missing.RecordType); writer.WriteString("record_id", missing.RecordId); writer.WriteString("state", missing.State); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("capabilities"); JsonSerializer.Serialize(writer, preview.Capabilities, JsonOptions);
            Distribution(writer, "completeness_distribution", selectedRecords.Select(record => record.Completeness));
            Distribution(writer, "content_state_distribution", selectedRecords.Select(record => record.ContentState));
            Distribution(writer, "retention_state_distribution", selectedRecords.Select(record => record.RetentionState));
            writer.WritePropertyName("processing_versions"); writer.WriteStartObject();
            foreach (var version in request.Snapshot.ProcessingVersions?.OrderBy(item => item.Key, StringComparer.Ordinal) ?? Enumerable.Empty<KeyValuePair<string, string>>())
                writer.WriteString(version.Key, version.Value);
            writer.WriteEndObject();
            writer.WritePropertyName("serialization"); writer.WriteStartObject();
            writer.WriteString("canonical_json", SanitizedExportContractVersions.CanonicalJson);
            writer.WriteString("archive", SanitizedExportContractVersions.Archive);
            writer.WriteString("checksum", SanitizedExportContractVersions.Checksum);
            writer.WriteEndObject();
            writer.WritePropertyName("compatibility"); writer.WriteStartObject();
            writer.WriteString("minimum_reader_major", SanitizedExportContractVersions.CompatibilityMinimum);
            writer.WriteString("maximum_reader_major", SanitizedExportContractVersions.CompatibilityMaximum);
            writer.WriteEndObject();
            writer.WritePropertyName("repository_safe_validation"); writer.WriteStartObject();
            writer.WriteString("producer_profile", SanitizedExportContractVersions.ProducerValidation);
            writer.WriteString("scanner_profile", SanitizedExportContractVersions.Scanner); writer.WriteString("result", "passed"); writer.WriteEndObject();
            writer.WritePropertyName("files"); writer.WriteStartArray();
            foreach (var entry in preview.Entries.OrderBy(item => item.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("path", entry.Path); writer.WriteString("record_type", entry.RecordType);
                writer.WriteString("record_id", entry.RecordId); writer.WriteNumber("size", entry.Size); writer.WriteString("sha256", entry.Sha256); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteSelection(Utf8JsonWriter writer, SanitizedExportSelection selection)
    {
        writer.WritePropertyName("selection"); writer.WriteStartObject();
        Strings(writer, "session_ids", selection.SessionIds); Strings(writer, "trace_ids", selection.TraceIds);
        Strings(writer, "source_surfaces", selection.SourceSurfaces); Strings(writer, "repository_names", selection.RepositoryNames);
        Strings(writer, "workspace_labels", selection.WorkspaceLabels); Strings(writer, "receipt_types", selection.ReceiptTypes);
        if (selection.StartInclusive is null) writer.WriteNull("start_inclusive"); else writer.WriteString("start_inclusive", Timestamp(selection.StartInclusive.Value));
        if (selection.EndExclusive is null) writer.WriteNull("end_exclusive"); else writer.WriteString("end_exclusive", Timestamp(selection.EndExclusive.Value));
        writer.WriteEndObject();
    }

    private static void Strings(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        writer.WritePropertyName(name); writer.WriteStartArray();
        foreach (var value in values?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>()) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void Distribution(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name); writer.WriteStartObject();
        foreach (var group in values.GroupBy(value => value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)) writer.WriteNumber(group.Key, group.Count());
        writer.WriteEndObject();
    }

    private static void NullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

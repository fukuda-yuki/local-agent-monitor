using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.RawReplay;

public sealed partial class RawReplayArchiveService
{
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] AllowedSources = ["collector-output", "langfuse-export", "raw-otlp"];
    private static readonly string[] AllowedCompatibilityStates =
        ["adapter_failure", "recognized_record_drop_detected", "schema_drift_detected", "supported", "supported_with_unknown_fields", "unknown", "unsupported_source_version"];
    private static readonly string[] AllowedCaptureContentStates = ["available", "not_captured", "redacted", "unknown", "unsupported"];
    private static readonly string[] AllowedSessionContentStates = ["available", "expired_pending_deletion", "not_captured", "redacted", "unsupported"];

    [GeneratedRegex("^(records/record-[0-9]{6}\\.json|session-content/content-[0-9]{6}\\.json)$", RegexOptions.CultureInvariant)]
    private static partial Regex PayloadPath();

    public RawReplayPreview Preview(RawReplaySnapshot snapshot, RawReplayExportControl control)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(control);
        if (ValidateControl(control) is { } controlError) return Failure(controlError);
        var prepared = Prepare(snapshot);
        if (prepared.ErrorCode is not null) return Failure(prepared.ErrorCode);
        if (RawReplayCredentialScanner.ContainsKnownCredential(snapshot.SnapshotId)
            || RawReplayCredentialScanner.ContainsKnownCredential(snapshot.LocalMonitorVersion)
            || snapshot.KnownMissing.Any(RawReplayCredentialScanner.ContainsKnownCredential)
            || prepared.Records.Any(RawReplayCredentialScanner.ContainsKnownCredential)
            || prepared.Contents.Any(RawReplayCredentialScanner.ContainsKnownCredential))
            return Failure("credential_material_detected");

        RawReplayOutputs outputs;
        try { outputs = RawReplayOutputBuilder.Build(prepared.Records); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or OverflowException)
        { return Failure("normalization_failed"); }

        var recordMembers = prepared.Records.Select(RawReplayJson.SerializeCanonical).ToArray();
        var contentMembers = prepared.Contents.Select(RawReplayJson.SerializeCanonical).ToArray();
        if (recordMembers.Any(bytes => bytes.Length > RawReplayLimits.MaximumRawRecordBytes)
            || contentMembers.Any(bytes => bytes.Length > RawReplayLimits.MaximumSessionContentBytes)) return Failure("entry_too_large");
        var estimated = recordMembers.Sum(bytes => (long)bytes.Length) + contentMembers.Sum(bytes => (long)bytes.Length);
        if (recordMembers.Length + contentMembers.Length > RawReplayLimits.MaximumPayloadEntries) return Failure("entry_limit_exceeded");
        if (estimated >= RawReplayLimits.MaximumArchiveBytes) return Failure("archive_too_large");

        var dates = prepared.Records.Select(record => record.ReceivedAt)
            .Concat(prepared.Contents.Select(content => content.CapturedAt)).Order().ToArray();
        var sourceVersions = prepared.Records.Select(SourceVersion)
            .Concat(prepared.Contents.Select(ContentSourceVersion))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var contentStates = prepared.Records.Select(record => record.Provenance.CaptureContentState)
            .Concat(prepared.Contents.Select(content => content.ContentState))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var filterStates = prepared.Records.Select(record => $"{record.Provenance.SecretFilterState}:{record.Provenance.SecretFilterVersion}")
            .Concat(prepared.Contents.Select(content => $"{content.SecretFilterState}:{content.SecretFilterVersion}"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var knownMissing = snapshot.KnownMissing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!ValidSummary(sourceVersions) || !ValidSummary(contentStates)
            || !ValidSummary(filterStates) || !ValidSummary(knownMissing)) return Failure("record_invalid");
        var binding = RawReplayJson.SerializeCanonical(new
        {
            schema_version = RawReplayContractVersions.ExportControl,
            profile = control.Profile,
            created_at = control.CreatedAt,
            selection = control.Selection,
            include_session_content = control.IncludeSessionContent,
            snapshot_id = snapshot.SnapshotId,
            local_monitor_version = snapshot.LocalMonitorVersion,
            records = recordMembers.Select(bytes => RawReplayHash.Sha256(bytes)).ToArray(),
            session_contents = contentMembers.Select(bytes => RawReplayHash.Sha256(bytes)).ToArray(),
            known_missing = knownMissing,
            normalization_version = RawReplayContractVersions.Normalization,
            projection_version = RawReplayContractVersions.Projection,
            dashboard_version = RawReplayContractVersions.Dashboard,
        });
        var digest = RawReplayHash.Framed("copilot-agent-observability/raw-local-replay-preview/v1", binding);
        return new(
            true, null, RawReplayWarnings.RawData, "raw", RawReplayContractVersions.BundleProfile,
            prepared.Records.Count, prepared.Contents.Count,
            dates.Length == 0 ? null : dates[0], dates.Length == 0 ? null : dates[^1].AddTicks(1),
            sourceVersions, contentStates, filterStates, knownMissing,
            RawReplayContractVersions.Normalization, RawReplayContractVersions.Projection, RawReplayContractVersions.Dashboard,
            outputs.NormalizedSha256, outputs.ProjectionSha256, outputs.DashboardSha256,
            estimated, digest);
    }

    public RawReplayResult Create(RawReplaySnapshot snapshot, RawReplayExportControl control)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(control);
        if (ValidateCommitControl(control) is { } controlError)
            return new(false, controlError, Failure(controlError), null, null, null);
        var preview = Preview(snapshot, control);
        if (!preview.Success) return new(false, preview.ErrorCode, preview, null, null, null);
        if (control.PreviewDigest != preview.PreviewDigest)
            return new(false, "preview_changed", preview, null, null, null);

        var prepared = Prepare(snapshot);
        var files = new List<(string Path, string Kind, byte[] Bytes)>();
        for (var index = 0; index < prepared.Records.Count; index++)
            files.Add(($"records/record-{index + 1:000000}.json", "raw_record", RawReplayJson.SerializeCanonical(prepared.Records[index])));
        for (var index = 0; index < prepared.Contents.Count; index++)
            files.Add(($"session-content/content-{index + 1:000000}.json", "session_event_content", RawReplayJson.SerializeCanonical(prepared.Contents[index])));

        var manifest = new RawReplayManifest(
            RawReplayContractVersions.Manifest,
            RawReplayContractVersions.BundleSchema,
            RawReplayContractVersions.BundleProfile,
            RawReplayContractVersions.CanonicalJson,
            RawReplayContractVersions.Archive,
            RawReplayContractVersions.Checksum,
            "raw",
            control.CreatedAt,
            snapshot.SnapshotId,
            snapshot.LocalMonitorVersion,
            prepared.Records.Count,
            prepared.Contents.Count,
            preview.StartInclusive,
            preview.EndExclusive,
            preview.SourceVersions,
            preview.ContentStates,
            preview.SecretFilterStates,
            preview.KnownMissing,
            preview.NormalizationVersion,
            preview.ProjectionVersion,
            preview.DashboardVersion,
            preview.ExpectedNormalizedSha256!,
            preview.ExpectedProjectionSha256!,
            preview.ExpectedDashboardSha256!,
            files.Select(file => new RawReplayManifestFile(file.Path, file.Kind, file.Bytes.LongLength, RawReplayHash.Sha256(file.Bytes))).ToArray());
        var manifestBytes = RawReplayJson.SerializeCanonical(manifest);
        if (manifestBytes.Length > RawReplayLimits.MaximumManifestBytes)
            return new(false, "manifest_too_large", preview, null, null, null);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", manifestBytes);
            foreach (var file in files) WriteEntry(archive, file.Path, file.Bytes);
        }
        var archiveBytes = output.ToArray();
        if (archiveBytes.LongLength > RawReplayLimits.MaximumArchiveBytes)
            return new(false, "archive_too_large", preview, null, null, null);
        var inspection = Inspect(archiveBytes);
        if (!inspection.Success) return new(false, inspection.ErrorCode ?? "publish_validation_failed", preview, null, null, null);
        return new(true, null, preview, manifestBytes, archiveBytes, RawReplayHash.Sha256(archiveBytes));
    }

    public RawReplayResult CreateAndPublish(RawReplaySnapshot snapshot, RawReplayExportControl control, string outputPath)
    {
        if (!ValidOutputName(outputPath))
            return new(false, "output_name_invalid", Failure("output_name_invalid"), null, null, null);
        var result = Create(snapshot, control);
        if (!result.Success) return result;
        var temporary = outputPath + ".partial";
        try
        {
            if (File.Exists(outputPath) || File.Exists(temporary)) return result with { Success = false, ErrorCode = "output_exists", ArchiveBytes = null, ArchiveSha256 = null, ManifestBytes = null };
            var parent = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return result with { Success = false, ErrorCode = "publish_failed", ArchiveBytes = null, ArchiveSha256 = null, ManifestBytes = null };
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(result.ArchiveBytes!);
                stream.Flush(flushToDisk: true);
            }
            if (!Inspect(File.ReadAllBytes(temporary)).Success) return result with { Success = false, ErrorCode = "publish_validation_failed", ArchiveBytes = null, ArchiveSha256 = null, ManifestBytes = null };
            File.Move(temporary, outputPath, overwrite: false);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return result with { Success = false, ErrorCode = "publish_failed", ArchiveBytes = null, ArchiveSha256 = null, ManifestBytes = null }; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    public RawReplayInspection Inspect(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.LongLength > RawReplayLimits.MaximumArchiveBytes) return InspectionFailure("archive_too_large");
        if (archiveBytes.Length < 22) return InspectionFailure("archive_invalid");
        try
        {
            using var archive = new ZipArchive(new MemoryStream(archiveBytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count is 0 or > RawReplayLimits.MaximumArchiveEntries) return InspectionFailure("entry_limit_exceeded");
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName != "manifest.json" && !PayloadPath().IsMatch(entry.FullName)) return InspectionFailure("entry_path_invalid");
            }
            if (archive.Entries[0].FullName != "manifest.json") return InspectionFailure("entry_order_invalid");
            var framingError = ValidateStoredArchive(archiveBytes, archive.Entries.Count);
            if (framingError is not null) return InspectionFailure(framingError);
            var manifestBytes = ReadEntry(archive.Entries[0], RawReplayLimits.MaximumManifestBytes);
            if (manifestBytes is null) return InspectionFailure("manifest_too_large");
            RawReplayManifest manifest;
            try { manifest = RawReplayJson.DeserializeExact<RawReplayManifest>(manifestBytes); }
            catch (JsonException) { return InspectionFailure("manifest_invalid"); }
            if (!RawReplayJson.IsCanonical(manifestBytes, manifest)) return InspectionFailure("manifest_not_canonical");
            if (manifest.Profile != RawReplayContractVersions.BundleProfile) return InspectionFailure("profile_invalid");
            if (manifest.SchemaVersion != RawReplayContractVersions.Manifest
                || manifest.BundleSchemaVersion != RawReplayContractVersions.BundleSchema
                || manifest.CanonicalJsonVersion != RawReplayContractVersions.CanonicalJson
                || manifest.ArchiveVersion != RawReplayContractVersions.Archive
                || manifest.ChecksumVersion != RawReplayContractVersions.Checksum
                || manifest.DataClassification != "raw") return InspectionFailure("schema_invalid");
            if (manifest.NormalizationVersion != RawReplayContractVersions.Normalization
                || manifest.ProjectionVersion != RawReplayContractVersions.Projection
                || manifest.DashboardVersion != RawReplayContractVersions.Dashboard) return InspectionFailure("version_mismatch");
            if (RawReplayCredentialScanner.ContainsKnownCredential(manifest.SnapshotId)
                || RawReplayCredentialScanner.ContainsKnownCredential(manifest.LocalMonitorVersion)
                || manifest.SourceVersions.Any(RawReplayCredentialScanner.ContainsKnownCredential)
                || manifest.ContentStates.Any(RawReplayCredentialScanner.ContainsKnownCredential)
                || manifest.SecretFilterStates.Any(RawReplayCredentialScanner.ContainsKnownCredential)
                || manifest.KnownMissing.Any(RawReplayCredentialScanner.ContainsKnownCredential))
                return InspectionFailure("credential_material_detected");
            if (!ValidIdentifier(manifest.SnapshotId) || !ValidIdentifier(manifest.LocalMonitorVersion)
                || !CanonicalSummary(manifest.SourceVersions) || !CanonicalSummary(manifest.ContentStates)
                || !CanonicalSummary(manifest.SecretFilterStates) || !CanonicalSummary(manifest.KnownMissing))
                return InspectionFailure("manifest_invalid");
            if (manifest.RawRecordCount < 0 || manifest.SessionContentCount < 0
                || manifest.RawRecordCount > RawReplayLimits.MaximumPayloadEntries
                || manifest.SessionContentCount > RawReplayLimits.MaximumPayloadEntries
                || manifest.Files.Count != archive.Entries.Count - 1
                || manifest.RawRecordCount + manifest.SessionContentCount != manifest.Files.Count
                || manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count)
                return InspectionFailure("inventory_mismatch");
            var expectedPaths = Enumerable.Range(1, manifest.RawRecordCount)
                .Select(index => (Path: $"records/record-{index:000000}.json", Kind: "raw_record"))
                .Concat(Enumerable.Range(1, manifest.SessionContentCount)
                    .Select(index => (Path: $"session-content/content-{index:000000}.json", Kind: "session_event_content")))
                .ToArray();
            if (!manifest.Files.Select(file => (file.Path, file.Kind)).SequenceEqual(expectedPaths))
                return InspectionFailure("inventory_mismatch");

            var records = new List<RawReplayRecord>();
            var contents = new List<RawReplaySessionContent>();
            long total = manifestBytes.LongLength;
            for (var index = 0; index < manifest.Files.Count; index++)
            {
                var descriptor = manifest.Files[index];
                var entry = archive.Entries[index + 1];
                if (descriptor.Path != entry.FullName || !IsSha256(descriptor.Sha256)
                    || descriptor.Kind is not ("raw_record" or "session_event_content")) return InspectionFailure("inventory_mismatch");
                var maximum = descriptor.Kind == "raw_record" ? RawReplayLimits.MaximumRawRecordBytes : RawReplayLimits.MaximumSessionContentBytes;
                var bytes = ReadEntry(entry, maximum);
                if (bytes is null || bytes.LongLength != descriptor.Size) return InspectionFailure("entry_size_mismatch");
                total += bytes.LongLength;
                if (total > RawReplayLimits.MaximumArchiveBytes) return InspectionFailure("archive_too_large");
                if (RawReplayHash.Sha256(bytes) != descriptor.Sha256) return InspectionFailure("checksum_mismatch");
                if (descriptor.Kind == "raw_record")
                {
                    RawReplayRecord record;
                    try { record = RawReplayJson.DeserializeExact<RawReplayRecord>(bytes); }
                    catch (JsonException) { return InspectionFailure("record_invalid"); }
                    if (!RawReplayJson.IsCanonical(bytes, record) || Validate(record) is not null) return InspectionFailure("record_invalid");
                    if (RawReplayCredentialScanner.ContainsKnownCredential(record)) return InspectionFailure("credential_material_detected");
                    records.Add(record);
                }
                else
                {
                    RawReplaySessionContent content;
                    try { content = RawReplayJson.DeserializeExact<RawReplaySessionContent>(bytes); }
                    catch (JsonException) { return InspectionFailure("record_invalid"); }
                    if (!RawReplayJson.IsCanonical(bytes, content) || Validate(content) is not null) return InspectionFailure("record_invalid");
                    if (RawReplayCredentialScanner.ContainsKnownCredential(content)) return InspectionFailure("credential_material_detected");
                    contents.Add(content);
                }
            }
            var prepared = Prepare(new RawReplaySnapshot(manifest.SnapshotId, manifest.CreatedAt, manifest.LocalMonitorVersion, records, contents, manifest.KnownMissing));
            if (prepared.ErrorCode is not null) return InspectionFailure(prepared.ErrorCode);
            if (records.Count != manifest.RawRecordCount || contents.Count != manifest.SessionContentCount)
                return InspectionFailure("inventory_mismatch");
            var dates = prepared.Records.Select(record => record.ReceivedAt)
                .Concat(prepared.Contents.Select(content => content.CapturedAt)).Order().ToArray();
            var sourceVersions = prepared.Records.Select(SourceVersion)
                .Concat(prepared.Contents.Select(ContentSourceVersion))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var contentStates = prepared.Records.Select(record => record.Provenance.CaptureContentState)
                .Concat(prepared.Contents.Select(content => content.ContentState))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var filterStates = prepared.Records.Select(record => $"{record.Provenance.SecretFilterState}:{record.Provenance.SecretFilterVersion}")
                .Concat(prepared.Contents.Select(content => $"{content.SecretFilterState}:{content.SecretFilterVersion}"))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (manifest.StartInclusive != (dates.Length == 0 ? null : dates[0])
                || manifest.EndExclusive != (dates.Length == 0 ? null : dates[^1].AddTicks(1))
                || !manifest.SourceVersions.SequenceEqual(sourceVersions, StringComparer.Ordinal)
                || !manifest.ContentStates.SequenceEqual(contentStates, StringComparer.Ordinal)
                || !manifest.SecretFilterStates.SequenceEqual(filterStates, StringComparer.Ordinal))
                return InspectionFailure("manifest_metadata_mismatch");
            RawReplayOutputs outputs;
            try { outputs = RawReplayOutputBuilder.Build(prepared.Records); }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or OverflowException)
            { return InspectionFailure("normalization_failed"); }
            if (outputs.NormalizedSha256 != manifest.ExpectedNormalizedSha256) return InspectionFailure("normalized_hash_mismatch");
            if (outputs.ProjectionSha256 != manifest.ExpectedProjectionSha256) return InspectionFailure("projection_hash_mismatch");
            if (outputs.DashboardSha256 != manifest.ExpectedDashboardSha256) return InspectionFailure("dashboard_hash_mismatch");
            return new(true, null, RawReplayHash.Sha256(archiveBytes), manifest.BundleSchemaVersion, manifest.Profile,
                prepared.Records.Count, prepared.Contents.Count, total, new(manifest, prepared.Records, prepared.Contents));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or NotSupportedException or OverflowException)
        { return InspectionFailure("archive_invalid"); }
    }

    private static PreparedSnapshot Prepare(RawReplaySnapshot snapshot)
    {
        if (!ValidIdentifier(snapshot.SnapshotId) || !ValidIdentifier(snapshot.LocalMonitorVersion)
            || snapshot.CapturedAt.Offset != TimeSpan.Zero || snapshot.Records is null || snapshot.SessionContents is null || snapshot.KnownMissing is null
            || snapshot.Records.Any(record => record is null) || snapshot.SessionContents.Any(content => content is null)
            || snapshot.KnownMissing.Any(value => !ValidKnownMissing(value)))
            return PreparedSnapshot.Failure("snapshot_invalid");
        var records = new List<RawReplayRecord>();
        foreach (var group in snapshot.Records.OrderBy(record => record.RawRecordId).GroupBy(record => record.RawRecordId))
        {
            var grouped = group.ToArray();
            if (grouped.Any(record => Validate(record) is not null)) return PreparedSnapshot.Failure("record_invalid");
            var canonical = grouped.Select(record => (Record: record, Bytes: RawReplayJson.SerializeCanonical(record))).ToArray();
            if (canonical.Skip(1).Any(item => !item.Bytes.AsSpan().SequenceEqual(canonical[0].Bytes))) return PreparedSnapshot.Failure("source_id_conflict");
            records.Add(canonical[0].Record);
        }
        var contents = new List<RawReplaySessionContent>();
        foreach (var group in snapshot.SessionContents.OrderBy(content => content.EventId, StringComparer.Ordinal).GroupBy(content => content.EventId, StringComparer.Ordinal))
        {
            var grouped = group.ToArray();
            if (grouped.Any(content => Validate(content) is not null)) return PreparedSnapshot.Failure("record_invalid");
            var canonical = grouped.Select(content => (Content: content, Bytes: RawReplayJson.SerializeCanonical(content))).ToArray();
            if (canonical.Skip(1).Any(item => !item.Bytes.AsSpan().SequenceEqual(canonical[0].Bytes))) return PreparedSnapshot.Failure("source_id_conflict");
            contents.Add(canonical[0].Content);
        }
        if (records.Count + contents.Count == 0) return PreparedSnapshot.Failure("selection_empty");
        return new(records, contents, null);
    }

    private static string? Validate(RawReplayRecord record) =>
        record.RawRecordId < 1 || !AllowedSources.Contains(record.Source, StringComparer.Ordinal)
        || record.ReceivedAt.Offset != TimeSpan.Zero || record.ReceivedAt == DateTimeOffset.MaxValue || record.SchemaVersion != 1
        || string.IsNullOrWhiteSpace(record.PayloadJson) || record.Provenance is null
        || !ValidOptionalIdentifier(record.Provenance.SourceSurface)
        || !ValidOptionalIdentifier(record.Provenance.SourceApplicationVersion)
        || !ValidOptionalIdentifier(record.Provenance.SourceAdapter)
        || !ValidOptionalIdentifier(record.Provenance.AdapterVersion)
        || !ValidOptionalIdentifier(record.Provenance.SchemaFingerprint)
        || !ValidOptionalIdentifier(record.Provenance.InventoryHash)
        || !AllowedCompatibilityStates.Contains(record.Provenance.CompatibilityState, StringComparer.Ordinal)
        || !AllowedCaptureContentStates.Contains(record.Provenance.CaptureContentState, StringComparer.Ordinal)
        || !ValidIdentifier(record.Provenance.SecretFilterState)
        || record.Provenance.SecretFilterVersion != RawReplayContractVersions.CredentialScanner
            ? "record_invalid" : null;

    private static string? Validate(RawReplaySessionContent content) =>
        string.IsNullOrWhiteSpace(content.EventId) || string.IsNullOrWhiteSpace(content.SessionId)
        || string.IsNullOrWhiteSpace(content.SourceAdapter) || string.IsNullOrWhiteSpace(content.SourceEventId)
        || content.OccurredAt.Offset != TimeSpan.Zero || content.CapturedAt.Offset != TimeSpan.Zero || content.CapturedAt == DateTimeOffset.MaxValue
        || content.ExpiresAt.Offset != TimeSpan.Zero
        || content.ExpiresAt < content.CapturedAt || string.IsNullOrWhiteSpace(content.ContentKind)
        || content.ContentJson is null || !AllowedSessionContentStates.Contains(content.ContentState, StringComparer.Ordinal)
        || !ValidIdentifier(content.SourceAdapter)
        || !ValidOptionalIdentifier(content.SourceApplicationVersion)
        || !ValidOptionalIdentifier(content.AdapterVersion)
        || !ValidOptionalIdentifier(content.SchemaFingerprint)
        || !ValidIdentifier(content.SecretFilterState) || content.SecretFilterVersion != RawReplayContractVersions.CredentialScanner
            ? "record_invalid" : null;

    internal static string? ValidateControl(RawReplayExportControl control)
    {
        if (control.SchemaVersion != RawReplayContractVersions.ExportControl) return "request_invalid";
        if (control.Profile != RawReplayContractVersions.BundleProfile) return "profile_invalid";
        if (control.SanitizedOnly) return "sanitized_only_denied";
        if (control.CreatedAt.Offset != TimeSpan.Zero || !ValidSelection(control.Selection, control.IncludeSessionContent)) return "request_invalid";
        return null;
    }

    internal static string? ValidateCommitControl(RawReplayExportControl control)
    {
        if (ValidateControl(control) is { } controlError) return controlError;
        if (control.Consent?.IsValid != true) return "consent_required";
        return IsSha256(control.PreviewDigest) ? null : "preview_changed";
    }

    private static bool ValidSelection(RawReplaySelection? selection, bool includeSessionContent)
    {
        if (selection is null || includeSessionContent && selection.SessionIds is not { Count: > 0 }
            || selection.StartInclusive is { Offset: var startOffset } && startOffset != TimeSpan.Zero
            || selection.EndExclusive is { Offset: var endOffset } && endOffset != TimeSpan.Zero
            || selection.StartInclusive is { } start && selection.EndExclusive is { } end && start >= end) return false;
        var any = selection.SessionIds is { Count: > 0 } || selection.TraceIds is { Count: > 0 }
            || selection.RawRecordIds is { Count: > 0 } || selection.Sources is { Count: > 0 }
            || selection.StartInclusive is not null || selection.EndExclusive is not null;
        return any
            && ValidStrings(selection.SessionIds)
            && ValidStrings(selection.TraceIds)
            && (selection.RawRecordIds is null || selection.RawRecordIds.Count <= RawReplayLimits.MaximumSelectionValues
                && selection.RawRecordIds.All(id => id > 0)
                && selection.RawRecordIds.Distinct().Count() == selection.RawRecordIds.Count)
            && (selection.Sources is null || ValidStrings(selection.Sources)
                && selection.Sources.All(source => AllowedSources.Contains(source, StringComparer.Ordinal)));
    }

    private static bool ValidStrings(IReadOnlyList<string>? values) => values is null
        || values.Count <= RawReplayLimits.MaximumSelectionValues
        && values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= RawReplayLimits.MaximumIdentifierLength
            && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#'))
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= RawReplayLimits.MaximumIdentifierLength
        && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#');

    private static bool ValidOptionalIdentifier(string? value) => value is null || ValidIdentifier(value);

    private static bool ValidKnownMissing(string? value) => value is { Length: >= 1 and <= 128 }
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool ValidSummary(IReadOnlyList<string> values) => values.Count <= RawReplayLimits.MaximumPayloadEntries
        && values.All(ValidSummaryValue);

    private static bool CanonicalSummary(IReadOnlyList<string> values) => ValidSummary(values)
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count
        && values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool ValidSummaryValue(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 1536 && !value.Any(char.IsControl);

    private static string SourceVersion(RawReplayRecord record) => string.Join('|',
        record.Provenance.SourceSurface ?? "missing", record.Provenance.SourceApplicationVersion ?? "missing",
        record.Provenance.SourceAdapter ?? "missing", record.Provenance.AdapterVersion ?? "missing",
        record.Provenance.SchemaFingerprint ?? "missing");

    private static string ContentSourceVersion(RawReplaySessionContent content) => string.Join('|',
        "session-event", content.SourceApplicationVersion ?? "missing", content.SourceAdapter,
        content.AdapterVersion ?? "missing", content.SchemaFingerprint ?? "missing");

    private static RawReplayPreview Failure(string code) => new(false, code, RawReplayWarnings.RawData, "raw",
        RawReplayContractVersions.BundleProfile, 0, 0, null, null, [], [], [], [],
        RawReplayContractVersions.Normalization, RawReplayContractVersions.Projection, RawReplayContractVersions.Dashboard,
        null, null, null, 0, null);

    private static RawReplayInspection InspectionFailure(string code) => new(false, code, null);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool ValidOutputName(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return false;
        var name = Path.GetFileName(outputPath);
        return name == "raw-local-replay.zip"
            || name.StartsWith("raw-local-replay-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal)
                && name[17..^4] is { Length: 12 } suffix && suffix.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = ArchiveTimestamp;
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[]? ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length < 0 || entry.Length > maximum || entry.Length > int.MaxValue || entry.CompressedLength != entry.Length) return null;
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
        const uint endSignature = 0x06054b50, centralSignature = 0x02014b50, localSignature = 0x04034b50;
        var minimumOffset = Math.Max(0, bytes.Length - 65_557); var endOffset = -1;
        for (var index = bytes.Length - 22; index >= minimumOffset; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == endSignature) { endOffset = index; break; }
        if (endOffset < 0) return "archive_invalid";
        var end = bytes.AsSpan(endOffset);
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(end[20..22]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[8..10]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..12]);
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..16]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..20]);
        if (commentLength != 0 || endOffset + 22 != bytes.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(end[4..6]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[6..8]) != 0
            || entriesOnDisk != expectedEntries || totalEntries != expectedEntries || centralOffset > int.MaxValue || centralSize > int.MaxValue
            || (long)centralOffset + centralSize != endOffset) return "archive_invalid";
        var cursor = (int)centralOffset; var expectedLocalOffset = 0;
        for (var entryIndex = 0; entryIndex < expectedEntries; entryIndex++)
        {
            if (cursor < 0 || cursor > endOffset - 46 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)) != centralSignature) return "archive_invalid";
            var central = bytes.AsSpan(cursor, 46); var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(central[42..46]);
            if (localOffset > int.MaxValue || localOffset != expectedLocalOffset || localOffset > bytes.Length - 30
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)localOffset, 4)) != localSignature) return "archive_invalid";
            if (BinaryPrimitives.ReadUInt16LittleEndian(central[10..12]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 8, 2)) != 0) return "compression_not_allowed";
            if (BinaryPrimitives.ReadUInt16LittleEndian(central[4..6]) != 20 || BinaryPrimitives.ReadUInt16LittleEndian(central[6..8]) != 20
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 4, 2)) != 20) return "archive_invalid";
            if (BinaryPrimitives.ReadUInt16LittleEndian(central[12..14]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(central[14..16]) != 0x21
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 10, 2)) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 12, 2)) != 0x21
                || BinaryPrimitives.ReadUInt32LittleEndian(central[38..42]) != 0) return "archive_metadata_invalid";
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(central[28..30]); var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(central[30..32]);
            var entryCommentLength = BinaryPrimitives.ReadUInt16LittleEndian(central[32..34]); var local = bytes.AsSpan((int)localOffset, 30);
            var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(local[26..28]); var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(local[28..30]);
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(local[18..22]); var centralFlags = BinaryPrimitives.ReadUInt16LittleEndian(central[8..10]);
            if (extraLength != 0 || entryCommentLength != 0 || localExtraLength != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(central[34..36]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(central[36..38]) != 0
                || (centralFlags & ~0x0800) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(local[6..8]) != centralFlags
                || !central[12..28].SequenceEqual(local[10..26]) || localNameLength != nameLength) return "archive_invalid";
            var centralName = bytes.AsSpan(cursor + 46, nameLength); var localName = bytes.AsSpan((int)localOffset + 30, localNameLength);
            var requiresUtf8 = centralName.ContainsAnyExceptInRange((byte)0x00, (byte)0x7f);
            if (!centralName.SequenceEqual(localName) || (((centralFlags & 0x0800) != 0) != requiresUtf8)) return "archive_invalid";
            var nextLocal = (long)localOffset + 30 + localNameLength + compressedSize;
            if (nextLocal > centralOffset) return "archive_invalid";
            expectedLocalOffset = (int)nextLocal;
            var next = (long)cursor + 46 + nameLength + extraLength + entryCommentLength;
            if (next > endOffset) return "archive_invalid";
            cursor = (int)next;
        }
        return cursor == endOffset && expectedLocalOffset == centralOffset ? null : "archive_invalid";
    }

    private sealed record PreparedSnapshot(IReadOnlyList<RawReplayRecord> Records, IReadOnlyList<RawReplaySessionContent> Contents, string? ErrorCode)
    {
        internal static PreparedSnapshot Failure(string code) => new([], [], code);
    }
}

using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.ConfigCli;

internal sealed class RetentionSensitiveBundleCaptureResult
{
    internal RetentionSensitiveBundleCaptureResult(string bundleId, string finalPath, IReadOnlyDictionary<string, SensitiveBundlePlanEntry> entries)
        => (BundleId, FinalPath, EntriesByCandidateId) = (bundleId, finalPath, entries);

    internal string BundleId { get; }
    internal string FinalPath { get; }
    internal IReadOnlyDictionary<string, SensitiveBundlePlanEntry> EntriesByCandidateId { get; }
    public override string ToString() => GetType().FullName!;
}

internal sealed class RetentionSensitiveBundleStore
{
    internal enum LegacyAdoptionOutcome { NoLegacy, AdoptedOrRecovered, Blocked }
    private const string Failure = "Sensitive bundle capture failed.";
    private readonly RetentionCatalogStore catalog;
    private readonly Action<string>? checkpoint;
    internal string? LastCaptureId { get; private set; }

    internal RetentionSensitiveBundleStore(RetentionCatalogStore catalog, Action<string>? checkpoint = null)
        => (this.catalog, this.checkpoint) = (catalog ?? throw new ArgumentNullException(nameof(catalog)), checkpoint);

    internal RetentionSensitiveBundleCaptureResult Capture(
        IReadOnlyList<SensitiveBundlePlanCandidate> candidates,
        IReadOnlyList<SensitiveBundleSourceInput> sourceInputs,
        string? parentLocator = null)
    {
        if (candidates is null || sourceInputs is null || candidates.All(static candidate => candidate?.Fragments.Count is not > 0)) throw new ArgumentException(Failure);
        var parent = CanonicalParent(parentLocator);
        try
        {
            if (AdoptLegacyBundles(parent) == LegacyAdoptionOutcome.Blocked) throw new InvalidOperationException(Failure);
            Recover();
            EnsureSafeAncestors(parent, requireExisting: false);
            var reservation = catalog.ReserveSensitiveBundle(parent);
            LastCaptureId = reservation.CaptureId;
            SensitiveBundlePlan plan;
            try { plan = SensitiveBundlePlanBuilder.Build(reservation, candidates, sourceInputs); }
            catch (SensitiveBundlePlanLimitException)
            {
                catalog.RecordSensitiveBundleBlocker(reservation, RetentionErrorCode.ItemLimitExceeded);
                throw new ArgumentException(Failure);
            }
            catch (ArgumentException)
            {
                catalog.AbandonReservedSensitiveBundle(reservation);
                throw;
            }
            if (catalog.PlanSensitiveBundle(reservation, plan.ToCapturePlanInput()) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
            EnsureSafeAncestors(parent, requireExisting: false);
            if (Exists(reservation.StagingLocator) || Exists(reservation.FinalLocator))
            {
                catalog.RecordSensitiveBundleBlocker(reservation, RetentionErrorCode.OwnershipMismatch);
                throw new InvalidOperationException();
            }
            checkpoint?.Invoke("before_staging_directory_create");
            if (Exists(reservation.StagingLocator) || Exists(reservation.FinalLocator))
            {
                catalog.RecordSensitiveBundleBlocker(reservation, RetentionErrorCode.OwnershipMismatch);
                throw new InvalidOperationException();
            }
            Directory.CreateDirectory(parent);
            EnsureDirectorySafe(parent);
            Directory.CreateDirectory(reservation.StagingLocator);
            EnsureDirectorySafe(reservation.StagingLocator);
            try { WritePlan(reservation, plan); }
            catch (RetentionSensitiveBundleOwnershipCollisionException)
            {
                catalog.RecordSensitiveBundleBlocker(reservation, RetentionErrorCode.OwnershipMismatch);
                throw;
            }
            Publish(reservation, plan.MarkerSha256, plan.ManifestSha256, plan.Members.Select(static member => member.ToCaptureMember()).ToArray());
            return new(reservation.CaptureId, reservation.FinalLocator, plan.EntriesByCandidateId);
        }
        catch (ArgumentException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(Failure);
        }
    }

    internal void Recover()
    {
        foreach (var snapshot in catalog.FindIncompleteSensitiveBundles(RetentionFileCaptureContracts.MaximumMemberCount))
        {
            if (snapshot.ErrorCode is not null) continue;
            try
            {
                var legacy = catalog.LoadLegacyBundleJournal(snapshot);
                if (legacy is null) Recover(snapshot); else RecoverLegacy(snapshot, legacy);
            }
            catch (IOException) { catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.CaptureIncomplete); }
            catch (UnauthorizedAccessException) { catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.CaptureIncomplete); }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or PathTooLongException)
            { catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.OwnershipMismatch); }
        }
    }

    // Legacy bundles are considered only when their explicitly configured parent is supplied.
    // The old writer had no marker, so adoption first proves its complete, closed layout.
    internal LegacyAdoptionOutcome AdoptLegacyBundles(string parentLocator)
    {
        var root = CanonicalParent(parentLocator);
        if (!Directory.Exists(root)) return LegacyAdoptionOutcome.NoLegacy;
        if (IsReparsePoint(root)) { if (File.Exists(Path.Combine(root, "manifest.json"))) catalog.RecordLegacySensitiveBundleBlocker(root); return File.Exists(Path.Combine(root, "manifest.json")) ? LegacyAdoptionOutcome.Blocked : LegacyAdoptionOutcome.NoLegacy; }
        var manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest) || File.Exists(Path.Combine(root, RetentionFileCaptureContracts.OwnerMarkerName))) return LegacyAdoptionOutcome.NoLegacy;
        try
        {
            if (!LooksLikeLegacyManifest(manifest)) return LegacyAdoptionOutcome.NoLegacy;
            AdoptLegacyBundle(root);
            return LegacyAdoptionOutcome.AdoptedOrRecovered;
        }
        catch (IOException) { catalog.RecordLegacySensitiveBundleBlocker(root); return LegacyAdoptionOutcome.Blocked; }
        catch (UnauthorizedAccessException) { catalog.RecordLegacySensitiveBundleBlocker(root); return LegacyAdoptionOutcome.Blocked; }
        catch (ArgumentException) { catalog.RecordLegacySensitiveBundleBlocker(root); return LegacyAdoptionOutcome.Blocked; }
        catch (JsonException) { catalog.RecordLegacySensitiveBundleBlocker(root); return LegacyAdoptionOutcome.Blocked; }
        catch (RetentionLegacyProofException) { catalog.RecordLegacySensitiveBundleBlocker(root); return LegacyAdoptionOutcome.Blocked; }
        catch (RetentionLegacyClaimLostException) { return LegacyAdoptionOutcome.Blocked; }
        catch (RetentionLegacyMigrationCrashException) { throw; }
        catch (InvalidOperationException) { catalog.RecordLegacySensitiveBundleBlocker(root); return LegacyAdoptionOutcome.Blocked; }
    }

    private static bool LooksLikeLegacyManifest(string manifest)
    {
        if (IsReparsePoint(manifest) || new FileInfo(manifest).Length > 1024 * 1024) throw new RetentionLegacyProofException();
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("created_at_utc", out _);
    }

    private void AdoptLegacyBundle(string legacyPath)
    {
        if (IsReparsePoint(legacyPath) || File.Exists(Path.Combine(legacyPath, RetentionFileCaptureContracts.OwnerMarkerName))) throw new RetentionLegacyProofException();
        var manifestPath = Path.Combine(legacyPath, "manifest.json");
        if (!File.Exists(manifestPath) || IsReparsePoint(manifestPath)) throw new RetentionLegacyProofException();
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new RetentionLegacyProofException();
        var manifest = File.ReadAllBytes(manifestPath);
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema_version", out var schema) || schema.GetInt32() != 1
            || !root.TryGetProperty("bundle_id", out var bundleId) || bundleId.GetString() != Path.GetFileName(legacyPath)
            || !root.TryGetProperty("generated_by_command", out var command) || command.GetString() != "generate-diagnosis-candidates"
            || !root.TryGetProperty("content_included", out var included) || included.ValueKind != JsonValueKind.True
            || !TryTimestamp(root, "created_at_utc", out var created)
            || !TryTimestamp(root, "expires_at_utc", out var expires)
            || expires != created.AddDays(7)
            || !ExactLegacyDeleteTarget(root, legacyPath)
            || !root.TryGetProperty("evidence_index", out var index) || index.ValueKind != JsonValueKind.Array) throw new RetentionLegacyProofException();

        var evidenceDirectory = Path.Combine(legacyPath, "evidence");
        if (!Directory.Exists(evidenceDirectory) || IsReparsePoint(evidenceDirectory)) throw new RetentionLegacyProofException();
        var expected = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        foreach (var entry in index.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("evidence_file", out var file) || !RetentionFileCaptureContracts.IsCanonicalRelativePath(file.GetString()) || !file.GetString()!.StartsWith("evidence/", StringComparison.Ordinal)) throw new RetentionLegacyProofException();
            expected.Add(file.GetString()!);
        }
        if (expected.Count != index.GetArrayLength() + 1) throw new RetentionLegacyProofException();
        var actual = Directory.EnumerateFiles(evidenceDirectory).Select(file => "evidence/" + Path.GetFileName(file)).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected) || Directory.EnumerateDirectories(evidenceDirectory).Any()) throw new RetentionLegacyProofException();
        if (Directory.EnumerateFileSystemEntries(legacyPath).Select(Path.GetFileName).Any(name => name is not "manifest.json" and not "evidence")) throw new RetentionLegacyProofException();
        if (expected.Count + 2 > RetentionFileCaptureContracts.MaximumMemberCount) throw new RetentionLegacyProofException();
        var evidenceFiles = expected.Where(static value => value != "manifest.json").OrderBy(static value => value, StringComparer.Ordinal).Select(relative => (Relative: relative, Path: Path.Combine(legacyPath, relative.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        var evidenceLengths = evidenceFiles.Select(file => (file.Relative, file.Path, Length: LegacyFileLength(file.Path))).ToArray();
        if (evidenceLengths.Sum(file => file.Length) + manifest.LongLength > RetentionFileCaptureContracts.MaximumMemberBytes) throw new RetentionLegacyProofException();
        checkpoint?.Invoke("legacy_evidence_preflight_complete");

        var legacyDigest = SHA256.HashData(manifest);
        var reservation = catalog.ReserveSensitiveBundle(legacyPath, created, legacyV1: true);
        manifest = WriteSanitizedLegacyManifest(reservation, index);
        var marker = RetentionFileCaptureOwnershipMarker.Create(reservation.StoreInstanceId, reservation.CaptureId, reservation.ReservedAtText, reservation.ReservedAtTicks, reservation.OwnerToken);
        var members = new List<RetentionFileCaptureMember>();
        var deletion = 0;
        foreach (var (relative, path, length) in evidenceLengths)
        {
            members.Add(new(members.Count, relative, RetentionFileCaptureMemberKind.File, length, HashLegacyEvidence(path, length), deletion++));
        }
        members.Add(new(members.Count, "manifest.json", RetentionFileCaptureMemberKind.File, manifest.Length, SHA256.HashData(manifest), deletion++));
        members.Add(new(members.Count, "evidence", RetentionFileCaptureMemberKind.Directory, null, null, deletion++));
        members.Add(new(members.Count, RetentionFileCaptureContracts.OwnerMarkerName, RetentionFileCaptureMemberKind.OwnerMarker, null, null, deletion));
        var plan = new RetentionFileCapturePlanInput(SHA256.HashData(marker), SHA256.HashData(manifest), members);
        if (!plan.IsValid) throw new InvalidOperationException();
        var claim = catalog.PlanLegacySensitiveBundle(reservation, plan, legacyDigest);
        if (claim == RetentionCaptureMutationDisposition.StaleNoOp)
        {
            catalog.AbandonReservedSensitiveBundle(reservation);
            throw new RetentionLegacyClaimLostException();
        }
        if (claim != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
        checkpoint?.Invoke("legacy_atomic_journal_claimed");
        var persisted = catalog.LoadIncompleteSensitiveBundle(reservation.CaptureId) ?? throw new InvalidOperationException();
        RecoverLegacy(persisted, catalog.LoadLegacyBundleJournal(persisted)!);
    }

    private static bool TryTimestamp(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && DateTimeOffset.TryParseExact(field.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
    private static bool ExactLegacyDeleteTarget(JsonElement root, string path) => root.TryGetProperty("delete_target_paths", out var paths) && paths.ValueKind == JsonValueKind.Array && paths.GetArrayLength() == 1 && paths[0].ValueKind == JsonValueKind.String && string.Equals(paths[0].GetString(), Path.GetFullPath(path), StringComparison.Ordinal);
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static long LegacyFileLength(string path)
    {
        if (IsReparsePoint(path)) throw new RetentionLegacyProofException();
        var length = new FileInfo(path).Length;
        if (length < 0 || length > RetentionFileCaptureContracts.MaximumMemberBytes) throw new RetentionLegacyProofException();
        return length;
    }
    private static byte[] HashLegacyEvidence(string path, long expectedLength)
    {
        if (IsReparsePoint(path)) throw new RetentionLegacyProofException();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (stream.Length != expectedLength) throw new RetentionLegacyProofException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long read = 0;
        while (true) { var count = stream.Read(buffer, 0, buffer.Length); if (count == 0) break; read += count; if (read > expectedLength) throw new RetentionLegacyProofException(); hash.AppendData(buffer, 0, count); }
        if (read != expectedLength || stream.Length != expectedLength) throw new RetentionLegacyProofException();
        return hash.GetHashAndReset();
    }
    private static byte[] WriteSanitizedLegacyManifest(RetentionFileCaptureReservation reservation, JsonElement index)
        => WriteSanitizedLegacyManifest((IRetentionFileCaptureCapability)reservation, index, reservation.ReservedAtText);
    private static byte[] WriteSanitizedLegacyManifest(IRetentionFileCaptureCapability capability, JsonElement index, string? reservedAt = null)
    {
        var timestamp = reservedAt ?? (capability as RetentionFileCaptureRecoverySnapshot)?.ReservedAtText ?? (capability as RetentionFileCaptureReservation)?.ReservedAtText ?? throw new InvalidOperationException();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject(); writer.WriteNumber("schema_version", 1); writer.WriteString("bundle_id", capability.CaptureId); writer.WriteString("reserved_at_utc", timestamp); writer.WriteString("expires_at_utc", DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.None).AddDays(7).ToString("O", CultureInfo.InvariantCulture)); writer.WriteString("generated_by_command", "generate-diagnosis-candidates"); writer.WriteBoolean("content_included", true); writer.WritePropertyName("evidence_index"); writer.WriteStartArray();
        foreach (var entry in index.EnumerateArray()) { writer.WriteStartObject(); writer.WriteString("evidence_file", entry.GetProperty("evidence_file").GetString()); writer.WriteEndObject(); }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); return stream.ToArray();
    }

    private void RecoverLegacy(IRetentionFileCaptureCapability capability, RetentionCatalogStore.LegacyBundleJournal journal)
    {
        var snapshot = capability as RetentionFileCaptureRecoverySnapshot;
        var reservation = capability as RetentionFileCaptureReservation;
        var parent = snapshot?.ParentLocator ?? reservation!.ParentLocator;
        var staging = snapshot?.StagingLocator ?? reservation!.StagingLocator;
        var final = snapshot?.FinalLocator ?? reservation!.FinalLocator;
        var marker = RetentionFileCaptureOwnershipMarker.Create(capability.StoreInstanceId, capability.CaptureId, snapshot?.ReservedAtText ?? reservation!.ReservedAtText, snapshot?.ReservedAtTicks ?? reservation!.ReservedAtTicks, capability.OwnerToken);
        var plan = snapshot?.Members ?? reservation!.Members;
        var manifestDigest = snapshot?.ManifestSha256 ?? reservation!.ManifestSha256;
        if (manifestDigest is null || plan.Count == 0 || journal.LegacyStagingLocator != staging) throw new InvalidOperationException();

        var phase = journal.Subphase;
        if (phase == RetentionCatalogStore.LegacyBundleSubphase.RootRenamePending)
        {
            checkpoint?.Invoke("legacy_before_root_to_staging_move");
            if (Directory.Exists(parent) && !Directory.Exists(staging)) Directory.Move(parent, staging);
            else if (!Directory.Exists(parent) && Directory.Exists(staging) && LegacyManifestMatches(staging, journal.LegacyManifestSha256)) { }
            else throw new InvalidOperationException();
            checkpoint?.Invoke("legacy_after_root_to_staging_move");
            AdvanceLegacy(capability, phase); phase++;
        }
        if (phase == RetentionCatalogStore.LegacyBundleSubphase.RootRenamed)
        {
            if (!Directory.Exists(staging) || IsReparse(staging) || !LegacyManifestMatches(staging, journal.LegacyManifestSha256)) throw new InvalidOperationException();
            checkpoint?.Invoke("legacy_before_parent_recreate");
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            EnsureDirectorySafe(parent);
            checkpoint?.Invoke("legacy_after_parent_recreate");
            AdvanceLegacy(capability, phase); phase++;
        }
        if (phase == RetentionCatalogStore.LegacyBundleSubphase.ParentRecreated)
        {
            EnsureSafeAncestors(parent, requireExisting: true);
            if (!Directory.Exists(staging) && Directory.Exists(final) && LegacyManifestMatches(final, journal.LegacyManifestSha256)) { AdvanceLegacy(capability, phase); phase++; }
            else if (Directory.EnumerateFileSystemEntries(parent).Any()) throw new InvalidOperationException();
        }
        if (phase == RetentionCatalogStore.LegacyBundleSubphase.ParentRecreated)
        {
            checkpoint?.Invoke("legacy_before_staging_to_final_move");
            if (Directory.Exists(staging) && !Directory.Exists(final)) Directory.Move(staging, final);
            else if (!Directory.Exists(staging) && Directory.Exists(final) && LegacyManifestMatches(final, journal.LegacyManifestSha256)) { }
            else throw new InvalidOperationException();
            checkpoint?.Invoke("legacy_after_staging_to_final_move");
            AdvanceLegacy(capability, phase); phase++;
        }
        if (phase == RetentionCatalogStore.LegacyBundleSubphase.FinalMoved)
        {
            ReplaceLegacyManifestAtomically(final, journal.LegacyManifestSha256, journal.ReplacementTempLocator, capability, manifestDigest);
            checkpoint?.Invoke("legacy_after_manifest_replace");
            AdvanceLegacy(capability, phase); phase++;
        }
        if (phase == RetentionCatalogStore.LegacyBundleSubphase.ManifestReplaced)
        {
            if (!FileMatches(Path.Combine(final, "manifest.json"), manifestDigest, plan.Single(member => member.RelativePath == "manifest.json").ByteLength!.Value)) throw new InvalidOperationException();
            var markerPath = Path.Combine(final, RetentionFileCaptureContracts.OwnerMarkerName);
            if (!File.Exists(markerPath)) using (var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(marker); stream.Flush(true); }
            if (!FileMatches(markerPath, SHA256.HashData(marker), marker.Length)) throw new InvalidOperationException();
            checkpoint?.Invoke("legacy_after_marker_write");
            AdvanceLegacy(capability, phase); phase++;
        }
        if (phase == RetentionCatalogStore.LegacyBundleSubphase.MarkerWritten)
        {
            if (snapshot is not null) AdvanceToEnd(snapshot); else for (var cursor = 0; cursor < plan.Count; cursor++) if (catalog.AdvanceSensitiveBundleCursor(capability, cursor) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
            if (catalog.TransitionSensitiveBundle(capability, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, plan.Count) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
            if (catalog.CompleteSensitiveBundle(capability, SHA256.HashData(marker), manifestDigest) is not (RetentionCaptureMutationDisposition.Applied or RetentionCaptureMutationDisposition.NoOpAlreadyFinalized)) throw new InvalidOperationException();
            AdvanceLegacy(capability, phase);
            checkpoint?.Invoke("legacy_after_catalog_completion");
        }
    }

    private void AdvanceLegacy(IRetentionFileCaptureCapability capability, RetentionCatalogStore.LegacyBundleSubphase phase)
    {
        if (catalog.AdvanceLegacyBundleSubphase(capability, phase, phase + 1) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
    }
    private static bool LegacyManifestMatches(string root, byte[] expected) { var path = Path.Combine(root, "manifest.json"); return File.Exists(path) && !IsReparsePoint(path) && new FileInfo(path).Length <= 1024 * 1024 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), expected); }
    private void ReplaceLegacyManifestAtomically(string root, byte[] expectedLegacy, string temporary, IRetentionFileCaptureCapability capability, byte[] expectedCurrent)
    {
        var manifest = Path.Combine(root, "manifest.json");
        if (!string.Equals(Path.GetDirectoryName(temporary), root, StringComparison.Ordinal)) throw new InvalidOperationException();
        if (IsReparsePoint(manifest) || new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidOperationException();
        var current = File.ReadAllBytes(manifest);
        var currentDigest = SHA256.HashData(current);
        var temporaryExists = File.Exists(temporary);
        if (temporaryExists && (IsReparsePoint(temporary) || new FileInfo(temporary).Length > 1024 * 1024)) throw new InvalidOperationException();
        if (CryptographicOperations.FixedTimeEquals(currentDigest, expectedCurrent))
        {
            if (temporaryExists && !FileMatches(temporary, expectedCurrent, new FileInfo(temporary).Length)) throw new InvalidOperationException();
            if (temporaryExists) File.Delete(temporary);
            return;
        }
        if (!CryptographicOperations.FixedTimeEquals(currentDigest, expectedLegacy)) throw new InvalidOperationException();
        if (temporaryExists)
        {
            if (!FileMatches(temporary, expectedCurrent, new FileInfo(temporary).Length)) throw new InvalidOperationException();
            checkpoint?.Invoke("legacy_manifest_temp_recovery_before_replace");
            File.Move(temporary, manifest, overwrite: true);
            checkpoint?.Invoke("legacy_manifest_temp_recovery_after_replace");
            return;
        }
        using var document = JsonDocument.Parse(current);
        if (!document.RootElement.TryGetProperty("evidence_index", out var index) || index.ValueKind != JsonValueKind.Array) throw new InvalidOperationException();
        var replacement = WriteSanitizedLegacyManifest(capability, index);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(replacement), expectedCurrent)) throw new InvalidOperationException();
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(replacement); stream.Flush(true); }
        checkpoint?.Invoke("legacy_manifest_temp_flushed");
        File.Move(temporary, manifest, overwrite: true);
        checkpoint?.Invoke("legacy_manifest_atomically_replaced");
    }

    private void Recover(RetentionFileCaptureRecoverySnapshot snapshot)
    {
        if (snapshot.Phase == RetentionCapturePhase.PublishedPendingCatalog && !Directory.Exists(snapshot.ParentLocator) && Directory.Exists(snapshot.StagingLocator)
            && string.Equals(Path.GetDirectoryName(snapshot.ParentLocator), Path.GetDirectoryName(snapshot.StagingLocator), StringComparison.Ordinal))
            Directory.CreateDirectory(snapshot.ParentLocator);
        EnsureSafeAncestors(snapshot.ParentLocator, requireExisting: false);
        var stagingExists = Exists(snapshot.StagingLocator);
        var finalExists = Exists(snapshot.FinalLocator);
        if (snapshot.Phase == RetentionCapturePhase.Reserved)
        {
            if (!stagingExists && !finalExists) { catalog.AbandonReservedSensitiveBundle(snapshot); return; }
            catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.OwnershipMismatch); return;
        }
        if (snapshot.Phase == RetentionCapturePhase.Staging)
        {
            if (!stagingExists || finalExists) { catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.OwnershipMismatch); return; }
            var state = Verify(snapshot.StagingLocator, snapshot);
            if (!state.Owned || state.Unexpected) { catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.OwnershipMismatch); return; }
            if (state.Complete)
            {
                AdvanceToEnd(snapshot);
                if (catalog.TransitionSensitiveBundle(snapshot, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, snapshot.PlannedMemberCount) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
                Publish(snapshot, snapshot.MarkerSha256!, snapshot.ManifestSha256!, alreadyPublishedIntent: true); return;
            }
            CleanupIncomplete(snapshot);
            catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.CaptureIncomplete);
            return;
        }
        if (snapshot.Phase == RetentionCapturePhase.PublishedPendingCatalog)
        {
            if (stagingExists == finalExists) { catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.OwnershipMismatch); return; }
            var location = stagingExists ? snapshot.StagingLocator : snapshot.FinalLocator;
            var state = Verify(location, snapshot);
            if (!state.Owned || state.Unexpected || !state.Complete) { catalog.RecordSensitiveBundleBlocker(snapshot, RetentionErrorCode.OwnershipMismatch); return; }
            if (stagingExists)
            {
                EnsureSafeAncestors(snapshot.ParentLocator, requireExisting: true);
                Directory.Move(snapshot.StagingLocator, snapshot.FinalLocator);
                state = Verify(snapshot.FinalLocator, snapshot);
                if (!state.Owned || state.Unexpected || !state.Complete) throw new InvalidOperationException();
            }
            Complete(snapshot, snapshot.MarkerSha256!, snapshot.ManifestSha256!);
        }
    }

    private void WritePlan(RetentionFileCaptureReservation reservation, SensitiveBundlePlan plan)
    {
        foreach (var member in plan.Members.OrderBy(static member => member.Ordinal))
        {
            var target = MemberPath(reservation.StagingLocator, member.RelativePath);
            EnsureSafeAncestorsForMember(reservation.StagingLocator, member.RelativePath);
            if (member.Kind == RetentionFileCaptureMemberKind.Directory)
            {
                checkpoint?.Invoke($"before_directory_member_{member.Ordinal}_create");
                if (Exists(target)) throw new RetentionSensitiveBundleOwnershipCollisionException();
                Directory.CreateDirectory(target); EnsureDirectorySafe(target);
            }
            else
            {
                var bytes = member.Utf8;
                try
                {
                    using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                    stream.Write(bytes); stream.Flush(flushToDisk: true);
                }
                catch (IOException) when (Exists(target)) { throw new RetentionSensitiveBundleOwnershipCollisionException(); }
                VerifyFile(target, member.Kind == RetentionFileCaptureMemberKind.OwnerMarker ? SHA256.HashData(bytes) : member.Sha256!, bytes.Length);
            }
            checkpoint?.Invoke($"member_{member.Ordinal}_verified_before_cursor");
            if (catalog.AdvanceSensitiveBundleCursor(reservation, member.Ordinal) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
        }
    }

    private void Publish(IRetentionFileCaptureCapability capability, byte[] marker, byte[] manifest, IReadOnlyList<RetentionFileCaptureMember>? plannedMembers = null, bool alreadyPublishedIntent = false)
    {
        var snapshot = capability as RetentionFileCaptureRecoverySnapshot;
        var reservation = capability as RetentionFileCaptureReservation;
        var cursor = snapshot?.PlannedMemberCount ?? plannedMembers!.Count;
        var staging = snapshot?.StagingLocator ?? reservation!.StagingLocator;
        var final = snapshot?.FinalLocator ?? reservation!.FinalLocator;
        var parent = snapshot?.ParentLocator ?? reservation!.ParentLocator;
        if (!alreadyPublishedIntent)
        {
            if (catalog.TransitionSensitiveBundle(capability, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, cursor) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
            checkpoint?.Invoke("published_intent_before_move");
        }
        EnsureSafeAncestors(parent, requireExisting: true);
        if (Exists(final)) throw new InvalidOperationException();
        Directory.Move(staging, final);
        var complete = snapshot is null
            ? Verify(final, plannedMembers!, marker, manifest, reservation!.StoreInstanceId, reservation.CaptureId, reservation.ReservedAtText, reservation.ReservedAtTicks, reservation.OwnerToken)
            : Verify(final, snapshot);
        if (!complete.Owned || complete.Unexpected || !complete.Complete) throw new InvalidOperationException();
        checkpoint?.Invoke("moved_before_completion");
        Complete(capability, marker, manifest);
    }

    private void Complete(IRetentionFileCaptureCapability capability, byte[] marker, byte[] manifest)
    {
        if (catalog.CompleteSensitiveBundle(capability, marker, manifest) is not (RetentionCaptureMutationDisposition.Applied or RetentionCaptureMutationDisposition.NoOpAlreadyFinalized)) throw new InvalidOperationException();
    }

    private void AdvanceToEnd(RetentionFileCaptureRecoverySnapshot snapshot)
    {
        var cursor = snapshot.DurableCursor ?? 0;
        while (cursor < snapshot.PlannedMemberCount)
        {
            if (catalog.AdvanceSensitiveBundleCursor(snapshot, cursor) != RetentionCaptureMutationDisposition.Applied) throw new InvalidOperationException();
            cursor++;
        }
    }

    private void CleanupIncomplete(RetentionFileCaptureRecoverySnapshot snapshot)
    {
        foreach (var member in snapshot.Members.OrderBy(static member => member.DeletionOrder))
        {
            var current = Verify(snapshot.StagingLocator, snapshot);
            if (!current.Owned || current.Unexpected) throw new InvalidOperationException();
            if (!current.Present.Contains(member.RelativePath)) continue;
            var path = MemberPath(snapshot.StagingLocator, member.RelativePath);
            checkpoint?.Invoke($"cleanup_before_delete_{member.RelativePath}");
            if (member.Kind == RetentionFileCaptureMemberKind.Directory) Directory.Delete(path, recursive: false);
            else File.Delete(path);
        }
        if (Directory.EnumerateFileSystemEntries(snapshot.StagingLocator).Any()) throw new InvalidOperationException();
        Directory.Delete(snapshot.StagingLocator, recursive: false);
    }

    private static Verification Verify(string root, RetentionFileCaptureRecoverySnapshot snapshot)
        => Verify(root, snapshot.Members, snapshot.MarkerSha256, snapshot.ManifestSha256, snapshot.StoreInstanceId, snapshot.CaptureId, snapshot.ReservedAtText, snapshot.ReservedAtTicks, snapshot.OwnerToken);

    private static Verification Verify(string root, IReadOnlyList<RetentionFileCaptureMember> members, byte[]? markerSha, byte[]? manifestSha, string store, string id, string reserved, long ticks, byte[] token)
    {
        if (!Directory.Exists(root) || IsReparse(root) || markerSha is not { Length: 32 } || manifestSha is not { Length: 32 }) return new(false, true, false, new HashSet<string>());
        var expected = members.ToDictionary(static member => member.RelativePath, StringComparer.Ordinal);
        var present = EnumerateExact(root);
        if (present is null || present.Any(path => !expected.ContainsKey(path))) return new(false, true, false, new HashSet<string>());
        var marker = RetentionFileCaptureOwnershipMarker.Create(store, id, reserved, ticks, token);
        foreach (var relative in present)
        {
            var member = expected[relative]; var path = MemberPath(root, relative);
            if (IsReparse(path) || (member.Kind == RetentionFileCaptureMemberKind.Directory ? !Directory.Exists(path) : !File.Exists(path))) return new(false, false, false, present);
            if (member.Kind != RetentionFileCaptureMemberKind.Directory)
            {
                var digest = member.Kind == RetentionFileCaptureMemberKind.OwnerMarker ? markerSha : member.Sha256;
                if (digest is null || !FileMatches(path, digest, member.Kind == RetentionFileCaptureMemberKind.OwnerMarker ? marker.LongLength : member.ByteLength!.Value)) return new(false, false, false, present);
                if (member.Kind == RetentionFileCaptureMemberKind.OwnerMarker && !CryptographicOperations.FixedTimeEquals(File.ReadAllBytes(path), marker)) return new(false, false, false, present);
            }
        }
        var owned = present.Contains(RetentionFileCaptureContracts.OwnerMarkerName);
        return new(owned, false, present.Count == expected.Count, present);
    }

    private static HashSet<string>? EnumerateExact(string root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Path, string Relative)>(); pending.Push((root, ""));
        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            if (IsReparse(directory)) return null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry); var relative = prefix.Length == 0 ? name : $"{prefix}/{name}";
                if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(relative) || result.Count >= RetentionFileCaptureContracts.MaximumMemberCount || IsReparse(entry)) return null;
                result.Add(relative);
                if (Directory.Exists(entry)) pending.Push((entry, relative));
            }
        }
        return result;
    }

    private static bool FileMatches(string path, byte[] expected, long length)
    {
        var info = new FileInfo(path);
        return info.Length == length && CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), expected);
    }

    private static void VerifyFile(string path, byte[] expected, long length)
    {
        if (!FileMatches(path, expected, length)) throw new IOException();
    }

    private static string CanonicalParent(string? requested)
    {
        try { return Path.GetFullPath(requested ?? Path.Combine(Path.GetTempPath(), "copilot-agent-observability", "sensitive-bundles")); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException(Failure); }
    }

    private static string MemberPath(string root, string relative)
    {
        if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(relative)) throw new InvalidOperationException();
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var relativeCheck = Path.GetRelativePath(Path.GetFullPath(root), full);
        if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(relativeCheck.Replace(Path.DirectorySeparatorChar, '/'))) throw new InvalidOperationException();
        return full;
    }

    private static void EnsureSafeAncestorsForMember(string root, string relative)
    {
        var target = MemberPath(root, relative); var parent = Path.GetDirectoryName(target)!;
        EnsureSafeAncestors(parent, requireExisting: true);
    }

    private static void EnsureSafeAncestors(string path, bool requireExisting)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(current)) EnsureDirectorySafe(current);
            else if (requireExisting) throw new InvalidOperationException();
            var next = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.Ordinal)) return;
            current = next;
        }
    }

    private static void EnsureDirectorySafe(string path)
    {
        if (!Directory.Exists(path) || IsReparse(path)) throw new InvalidOperationException();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static bool IsReparse(string path) => File.Exists(path) || Directory.Exists(path) ? (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 : false;
    private sealed record Verification(bool Owned, bool Unexpected, bool Complete, HashSet<string> Present);
}

internal sealed class RetentionSensitiveBundleOwnershipCollisionException : IOException
{
}

internal sealed class RetentionLegacyMigrationCrashException : Exception
{
}
internal sealed class RetentionLegacyProofException : Exception
{
}
internal sealed class RetentionLegacyClaimLostException : Exception
{
}

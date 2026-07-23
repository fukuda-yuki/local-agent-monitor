using System.IO.Compression;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupArchiveTests
{
    [Fact]
    public void Online_wal_backup_publishes_strict_raw_profile_with_consistent_snapshot()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        var bundle = Path.Combine(temp.DirectoryPath, "operator.backup.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(RuntimeBackupContractVersions.BundleProfile, result.BundleProfile);
        Assert.Contains(RuntimeBackupWarnings.RawContentIncluded, result.Warnings);
        Assert.Contains(RuntimeBackupWarnings.NotRepositorySafe, result.Warnings);
        Assert.Contains(RuntimeBackupWarnings.RetentionBackupNotPurged, result.Warnings);
        Assert.True(File.Exists(bundle));
        using var archive = ZipFile.OpenRead(bundle);
        Assert.Equal(["manifest.json", "database.sqlite"], archive.Entries.Select(entry => entry.FullName));
        Assert.All(archive.Entries, entry =>
        {
            Assert.Equal(entry.Length, entry.CompressedLength);
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
            Assert.Equal(0, entry.ExternalAttributes);
        });

        var inspection = new SqliteRuntimeBackupService(temp.TimeProvider).Inspect(bundle);
        Assert.True(inspection.Success, inspection.ErrorCode);
        Assert.Equal(RuntimeBackupContractVersions.Manifest, inspection.ManifestSchemaVersion);
        Assert.Equal(RuntimeBackupContractVersions.BundleSchema, inspection.BundleSchemaVersion);
        Assert.Equal(result.ArchiveSha256, inspection.ArchiveSha256);
        Assert.Equal(1, inspection.ComponentVersions["runtime_backup"]);
        Assert.DoesNotContain("source-value", Encoding.UTF8.GetString(RuntimeBackupJson.SerializeResult(inspection)), StringComparison.Ordinal);
        using var archiveHandle = new FileStream(bundle, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(result.ArchiveSha256, new SqliteRuntimeBackupService(temp.TimeProvider).Inspect(archiveHandle).ArchiveSha256);
    }

    [Fact]
    public void Manifest_records_the_actual_source_journal_mode()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
            RuntimeBackupTemp.Execute(connection, "PRAGMA journal_mode=DELETE;");
        var bundle = Path.Combine(temp.DirectoryPath, "operator.backup.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.True(result.Success, result.ErrorCode);
        using var archive = ZipFile.OpenRead(bundle);
        using var manifest = JsonDocument.Parse(RuntimeBackupTemp.Read(archive.GetEntry("manifest.json")!));
        Assert.Equal("delete", manifest.RootElement.GetProperty("snapshot").GetProperty("source_journal_mode").GetString());
    }

    [Fact]
    public void Online_snapshot_does_not_bypass_an_exclusive_delete_journal_writer()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("committed-value");
        var snapshot = Path.Combine(temp.DirectoryPath, "exclusive-writer-snapshot.sqlite");
        using var writer = RuntimeBackupTemp.Open(temp.DatabasePath);
        RuntimeBackupTemp.Execute(writer, "PRAGMA journal_mode=DELETE;");
        RuntimeBackupTemp.Execute(writer, "PRAGMA locking_mode=EXCLUSIVE;");
        RuntimeBackupTemp.Execute(writer, "BEGIN EXCLUSIVE;");

        try
        {
            var exception = Record.Exception(() =>
                SqliteRuntimeBackupService.OnlineSnapshot(temp.DatabasePath, snapshot));

            var sqlite = Assert.IsType<SqliteException>(exception);
            Assert.Contains(sqlite.SqliteErrorCode, new[] { 5, 6 });
        }
        finally
        {
            RuntimeBackupTemp.Execute(writer, "ROLLBACK;");
            if (File.Exists(snapshot)) File.Delete(snapshot);
        }
    }

    [Fact]
    public void Permission_failure_after_online_snapshot_fails_publication_and_cleans_owned_artifacts_without_receipt()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        var bundle = Path.Combine(temp.DirectoryPath, "permission-denied.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterOnlineSnapshot)
                throw new UnauthorizedAccessException("injected-permission-failure");
        });

        var result = service.CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.PublishFailed, result.ErrorCode);
        Assert.False(File.Exists(bundle));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, ".*.online-snapshot*"));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, ".runtime-backup-*.partial*"));
        using var connection = RuntimeBackupTemp.Open(temp.DatabasePath);
        Assert.Equal(0L, RuntimeBackupTemp.Scalar(connection, "SELECT COUNT(*) FROM runtime_backup_receipts;"));
    }

    [Fact]
    public void Non_busy_sqlite_snapshot_failure_is_sanitized_and_cleans_owned_transients()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        var bundle = Path.Combine(temp.DirectoryPath, "sqlite-io-failure.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.BeforeOnlineSnapshot)
                throw new SqliteException("private-sqlite-detail", 10);
        });

        var result = service.CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, result.ErrorCode);
        Assert.False(File.Exists(bundle));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, ".*.online-snapshot*"));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, ".runtime-backup-*.partial*"));
    }

    [Fact]
    public void Inspection_stage_storage_failure_is_not_reported_as_invalid_archive()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        var bundle = Path.Combine(temp.DirectoryPath, "inspection-stage-failure.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle).Success);
        var service = new SqliteRuntimeBackupService(temp.TimeProvider, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.BeforeInspectionStageFlush)
                throw new IOException("injected-inspection-stage-failure");
        });
        var target = Path.Combine(temp.DirectoryPath, "fresh", "monitor.db");

        var inspection = service.Inspect(bundle);
        var preview = service.Preview(bundle, target);
        var restore = service.Restore(bundle, target, new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, restore.ErrorCode);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, ".runtime-backup-inspect-*.sqlite*"));
    }

    [Fact]
    public void Archive_inspection_ignores_an_owned_online_snapshot_for_another_database()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        var bundle = Path.Combine(temp.DirectoryPath, "inspect-with-unrelated-owner.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle).Success);
        var unrelated = Path.Combine(temp.DirectoryPath, $".other.db.{new string('a', 32)}.online-snapshot");
        File.WriteAllText(unrelated, "private-other-database-snapshot");
        File.WriteAllText(unrelated + ".owner.v1", "runtime-backup-transient-owner.v1\n");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).Inspect(bundle);

        Assert.True(result.Success, result.ErrorCode);
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(unrelated + ".owner.v1"));
    }

    [Fact]
    public void Backup_window_binds_monotonic_live_cursors_around_the_online_wal_snapshot()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, "CREATE TABLE session_projection_state(projector_key TEXT PRIMARY KEY,projection_cursor INTEGER NULL);");
            RuntimeBackupTemp.Execute(connection, "INSERT INTO session_projection_state(projector_key,projection_cursor) VALUES('sessions',1);");
        }
        var bundle = Path.Combine(temp.DirectoryPath, "window.zip");
        SqliteConnection? overlappingWriter = null;
        SqliteTransaction? overlappingTransaction = null;
        var service = new SqliteRuntimeBackupService(temp.TimeProvider, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.BeforeOnlineSnapshot)
            {
                overlappingWriter = RuntimeBackupTemp.Open(temp.DatabasePath);
                overlappingTransaction = overlappingWriter.BeginTransaction(deferred: false);
                using var update = overlappingWriter.CreateCommand();
                update.Transaction = overlappingTransaction;
                update.CommandText = "UPDATE session_projection_state SET projection_cursor=2;";
                Assert.Equal(1, update.ExecuteNonQuery());
                using var observer = RuntimeBackupTemp.Open(temp.DatabasePath);
                Assert.Equal(1L, RuntimeBackupTemp.Scalar(observer, "SELECT projection_cursor FROM session_projection_state WHERE projector_key='sessions';"));
                return;
            }

            if (checkpoint != RuntimeBackupCheckpoints.AfterOnlineSnapshot) return;
            Assert.NotNull(overlappingWriter);
            Assert.NotNull(overlappingTransaction);
            using (var observer = RuntimeBackupTemp.Open(temp.DatabasePath))
                Assert.Equal(1L, RuntimeBackupTemp.Scalar(observer, "SELECT projection_cursor FROM session_projection_state WHERE projector_key='sessions';"));
            overlappingTransaction.Commit();
        });

        RuntimeBackupCreateResult result;
        try
        {
            result = service.CreateAndPublish(temp.DatabasePath, bundle);
        }
        finally
        {
            overlappingTransaction?.Dispose();
            overlappingWriter?.Dispose();
        }

        Assert.True(result.Success, result.ErrorCode);
        using var archive = ZipFile.OpenRead(bundle);
        using var manifest = JsonDocument.Parse(RuntimeBackupTemp.Read(archive.GetEntry("manifest.json")!));
        var root = manifest.RootElement;
        var window = root.GetProperty("backup_window");
        Assert.Equal(1, window.GetProperty("projection_cursors_at_start").GetProperty("session:sessions").GetInt64());
        Assert.Equal(1, root.GetProperty("projection_cursors").GetProperty("session:sessions").GetInt64());
        Assert.Equal(2, window.GetProperty("projection_cursors_at_end").GetProperty("session:sessions").GetInt64());
        Assert.Equal(temp.TimeProvider.GetUtcNow(), window.GetProperty("started_at").GetDateTimeOffset());
        Assert.Equal(temp.TimeProvider.GetUtcNow(), window.GetProperty("completed_at").GetDateTimeOffset());
        var capturedPath = Path.Combine(temp.DirectoryPath, "captured-window.sqlite");
        using (var capturedEntry = archive.GetEntry("database.sqlite")!.Open())
        using (var capturedFile = new FileStream(capturedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            capturedEntry.CopyTo(capturedFile);
        using var captured = RuntimeBackupTemp.Open(capturedPath);
        Assert.Equal(1L, RuntimeBackupTemp.Scalar(captured, "SELECT projection_cursor FROM session_projection_state WHERE projector_key='sessions';"));
    }

    [Fact]
    public void Restore_preview_projects_manifest_counts_retention_cursors_checksums_and_external_prerequisites()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source-value");
        var bundle = Path.Combine(temp.DirectoryPath, "operator.backup.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, bundle).Success);

        var preview = service.Preview(bundle, Path.Combine(temp.DirectoryPath, "fresh", "monitor.db"));

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(RuntimeBackupContractVersions.RestorePreview, preview.SchemaVersion);
        Assert.Equal("compatible", preview.CompatibilityReason);
        Assert.Contains("monitor_stopped", preview.DestinationPrerequisites);
        Assert.Equal(64, preview.DatabaseSha256?.Length);
        Assert.Equal("wal", preview.SourceJournalMode);
        Assert.Equal(1, preview.RowCounts!["runtime_probe"]);
        Assert.Empty(preview.ProjectionCursors!);
        Assert.Equal(0, preview.Retention!.TombstoneCount);
        Assert.Equal(5, preview.Retention.StoreKindCounts.Count);
        Assert.Equal(["ephemeral_runtime", "setup_storage", "proposal_apply", "operator_backups"], preview.ExternalState!.Select(item => item.Kind));
    }

    [Fact]
    public void Active_external_raw_store_fails_before_partial_publication()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        temp.InsertRetentionItem("sensitive_bundle", "expiring", readDenied: false, sourceItemId: new string('a', 32));
        var bundle = Path.Combine(temp.DirectoryPath, "must-not-exist.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRawStoreActive, result.ErrorCode);
        Assert.False(File.Exists(bundle));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, "*.partial"));
    }

    [Theory]
    [InlineData("sensitive_bundle")]
    [InlineData("analysis_sdk_directory")]
    public void Unresolved_external_capture_reservation_fails_before_publication(string kind)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        temp.InsertExternalReservation(temp.DatabasePath, kind);
        var bundle = Path.Combine(temp.DirectoryPath, $"{kind}-reservation.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRawStoreActive, result.ErrorCode);
        Assert.False(File.Exists(bundle));
        Assert.DoesNotContain(temp.DirectoryPath, Encoding.UTF8.GetString(RuntimeBackupJson.SerializeResult(result)), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sensitive_bundle", "complete")]
    [InlineData("analysis_sdk_directory", "sealed")]
    public void Orphan_terminal_external_capture_reservation_fails_before_publication(string kind, string phase)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        temp.InsertExternalReservation(temp.DatabasePath, kind, phase);
        var bundle = Path.Combine(temp.DirectoryPath, $"{kind}-orphan-terminal.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRawStoreActive, result.ErrorCode);
        Assert.False(File.Exists(bundle));
    }

    [Theory]
    [InlineData("sensitive_bundle", "complete")]
    [InlineData("analysis_sdk_directory", "sealed")]
    public void Terminal_external_capture_reservation_with_matching_deleted_item_is_backup_compatible(string kind, string phase)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        temp.InsertExternalReservation(temp.DatabasePath, kind, phase);
        temp.InsertDeletedExternalItem(temp.DatabasePath, kind);
        var bundle = Path.Combine(temp.DirectoryPath, $"{kind}-deleted-terminal.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.True(result.Success, result.ErrorCode);
        Assert.True(File.Exists(bundle));
    }

    [Fact]
    public void Forged_terminal_file_reservation_cannot_borrow_a_deleted_raw_item()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        temp.InsertExternalReservation(temp.DatabasePath, "sensitive_bundle", "complete");
        temp.InsertDeletedRawItem(temp.DatabasePath);
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, "PRAGMA ignore_check_constraints=ON;");
            RuntimeBackupTemp.Execute(connection, "UPDATE retention_file_capture_reservations SET store_kind='raw_record',source_item_id='999';");
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var bundle = Path.Combine(temp.DirectoryPath, "forged-file-terminal.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRawStoreActive, result.ErrorCode);
        Assert.False(File.Exists(bundle));
    }

    [Fact]
    public void Forged_terminal_capture_journal_cannot_borrow_a_deleted_raw_item()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        temp.InsertExternalReservation(temp.DatabasePath, "sensitive_bundle", "complete");
        temp.InsertDeletedExternalItem(temp.DatabasePath, "sensitive_bundle");
        temp.InsertDeletedRawItem(temp.DatabasePath);
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, $"INSERT INTO retention_capture_journal(item_id,phase,durable_cursor) VALUES('{new string('f', 32)}','complete',NULL);");
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var bundle = Path.Combine(temp.DirectoryPath, "forged-journal-terminal.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRawStoreActive, result.ErrorCode);
        Assert.False(File.Exists(bundle));
    }

    [Fact]
    public void Legacy_external_bundle_blocker_fails_before_publication()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        temp.InsertLegacyBundleBlocker(temp.DatabasePath);
        var bundle = Path.Combine(temp.DirectoryPath, "legacy-blocker.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRawStoreActive, result.ErrorCode);
        Assert.False(File.Exists(bundle));
        Assert.DoesNotContain(temp.DirectoryPath, Encoding.UTF8.GetString(RuntimeBackupJson.SerializeResult(result)), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData(".runtime-restore.lock")]
    [InlineData(".runtime-restore-stage")]
    [InlineData(".runtime-restore-rollback")]
    [InlineData(".runtime-restore-journal.json")]
    public void Backup_output_rejects_database_sidecar_and_restore_control_collisions(string suffix)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider)
            .CreateAndPublish(temp.DatabasePath, temp.DatabasePath + suffix);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
    }

    [Theory]
    [InlineData(".runtime-backup-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.partial.owner.v1")]
    [InlineData(".runtime-backup-inspect-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.sqlite.owner.v1")]
    [InlineData(".monitor.db.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.online-snapshot.owner.v1")]
    public void Backup_output_rejects_transient_owner_marker_namespace(string fileName)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var output = Path.Combine(temp.DirectoryPath, fileName);

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, output);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Windows_backup_output_rejects_mixed_case_transient_owner_marker_namespace()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var output = Path.Combine(
            temp.DirectoryPath,
            ".RUNTIME-BACKUP-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.PARTIAL.OWNER.V1");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider)
            .CreateAndPublish(temp.DatabasePath, output);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Cli_path_contract_rejects_non_native_lexical_forms_before_resolving_other_arguments()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var bundle = Path.Combine(temp.DirectoryPath, "valid-path.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, bundle).Success);
        var invalid = OperatingSystem.IsWindows()
            ? new[] { "relative.db", "C:drive-relative.db", "\\current-drive-rooted.db", "file:///C:/monitor.db", "\\\\server\\share\\monitor.db", "\\\\?\\C:\\monitor.db", "/tmp/foreign.db" }
            : new[] { "relative.db", "C:\\foreign.db", "file:///tmp/monitor.db", "//server/share/monitor.db", "\\\\server\\share\\monitor.db" };

        foreach (var database in invalid)
        {
            var preview = service.Preview(bundle, database);
            Assert.False(preview.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, preview.ErrorCode);
        }

        if (OperatingSystem.IsWindows())
        {
            var ads = service.CreateAndPublish(temp.DatabasePath, temp.DatabasePath + ":backup");
            Assert.False(ads.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, ads.ErrorCode);
        }
    }

    [Fact]
    public void Reparse_ancestor_is_rejected_as_a_cli_path_argument()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var bundle = Path.Combine(temp.DirectoryPath, "reparse-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, bundle).Success);
        var real = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "real-destination"));
        var link = Path.Combine(temp.DirectoryPath, "linked-destination");
        try { Directory.CreateSymbolicLink(link, real.FullName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw Xunit.Sdk.SkipException.ForSkip($"Cannot create directory reparse fixture: {exception.GetType().Name}"); }

        var preview = service.Preview(bundle, Path.Combine(link, "monitor.db"));

        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, preview.ErrorCode);
    }

    [Fact]
    public void Inspect_stream_rejects_reparse_ancestor_before_recovery_or_staging()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var original = Path.Combine(temp.DirectoryPath, "stream-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, original).Success);
        var real = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "stream-real"));
        var moved = Path.Combine(real.FullName, "stream-valid.zip");
        File.Move(original, moved);
        var link = Path.Combine(temp.DirectoryPath, "stream-link");
        try { Directory.CreateSymbolicLink(link, real.FullName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw Xunit.Sdk.SkipException.ForSkip($"Cannot create directory reparse fixture: {exception.GetType().Name}"); }
        using var stream = new FileStream(Path.Combine(link, "stream-valid.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = service.Inspect(stream);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(real.FullName, ".runtime-backup-inspect-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Unix_native_path_classification_rejects_devices_fifos_and_sockets_but_accepts_regular_controls()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        var bundle = Path.Combine(temp.DirectoryPath, "special-path-source.zip");
        Assert.True(service.CreateAndPublish(temp.DatabasePath, bundle).Success);
        var control = Path.Combine(temp.DirectoryPath, "runtime-backup.control");
        File.WriteAllText(control, "owned");
        var fifo = Path.Combine(temp.DirectoryPath, "runtime-backup.fifo");
        Assert.Equal(0, MkFifo(fifo, Convert.ToUInt32("600", 8)));
        var socketPath = Path.Combine(temp.DirectoryPath, "runtime-backup.socket");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        Assert.Equal(RuntimeBackupNativePathKind.RegularFile, RuntimeBackupNativePathClassifier.Read(control));
        Assert.Equal(RuntimeBackupNativePathKind.OtherOrUnavailable, RuntimeBackupNativePathClassifier.Read("/dev/null"));
        Assert.Equal(RuntimeBackupNativePathKind.OtherOrUnavailable, RuntimeBackupNativePathClassifier.Read(fifo));
        Assert.Equal(RuntimeBackupNativePathKind.OtherOrUnavailable, RuntimeBackupNativePathClassifier.Read(socketPath));
        foreach (var special in new[] { "/dev/null", fifo, socketPath })
        {
            var create = service.CreateAndPublish(temp.DatabasePath, special);
            var preview = service.Preview(bundle, special);
            var inspect = service.Inspect(special);
            Assert.False(create.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, create.ErrorCode);
            Assert.False(preview.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, preview.ErrorCode);
            Assert.False(inspect.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, inspect.ErrorCode);
        }
    }

    [Fact]
    public void Windows_reserved_device_names_are_rejected_in_every_path_segment_without_mutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        var before = Directory.EnumerateFileSystemEntries(temp.DirectoryPath).Order(StringComparer.Ordinal).ToArray();

        foreach (var output in new[]
                 {
                     Path.Combine(temp.DirectoryPath, "NUL"),
                     Path.Combine(temp.DirectoryPath, "COM1.txt", "backup.zip"),
                     Path.Combine(temp.DirectoryPath, "CONIN$", "backup.zip"),
                     Path.Combine(temp.DirectoryPath, "CONOUT$.txt", "backup.zip"),
                     Path.Combine(temp.DirectoryPath, "COM¹.txt", "backup.zip"),
                     Path.Combine(temp.DirectoryPath, "LPT³", "backup.zip"),
                 })
        {
            var result = service.CreateAndPublish(temp.DatabasePath, output);
            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        }

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(temp.DirectoryPath).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(DriveType.Unknown, false)]
    [InlineData(DriveType.NoRootDirectory, false)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.CDRom, true)]
    [InlineData(DriveType.Ram, true)]
    public void Windows_runtime_backup_path_requires_a_proven_local_drive_type(
        DriveType driveType,
        bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(expected, SqliteRuntimeBackupService.IsHostNativeAbsoluteLocalPath(
            @"Z:\monitor.db",
            _ => driveType));
    }

    [Fact]
    public void Unix_special_file_in_an_allowed_runtime_root_slot_fails_closed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var allowedName = Path.Combine(temp.DirectoryPath, "local-monitor.pid");
        Assert.Equal(0, MkFifo(allowedName, Convert.ToUInt32("600", 8)));
        var bundle = Path.Combine(temp.DirectoryPath, "special-runtime-root.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider)
            .CreateAndPublish(temp.DatabasePath, bundle);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe, result.ErrorCode);
        Assert.False(File.Exists(bundle));
    }

    [Fact]
    public void Unix_path_with_embedded_windows_separator_is_rejected_without_mutation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var bundle = Path.Combine(temp.DirectoryPath, "valid-before-foreign-path.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, bundle).Success);
        var before = Directory.EnumerateFileSystemEntries(temp.DirectoryPath).Order(StringComparer.Ordinal).ToArray();

        var preview = service.Preview(bundle, Path.Combine(temp.DirectoryPath, "foreign\\monitor.db"));

        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, preview.ErrorCode);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(temp.DirectoryPath).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("compressed")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("symlink-attributes")]
    [InlineData("corrupt")]
    [InlineData("duplicate-manifest-key")]
    [InlineData("invalid-journal-mode")]
    [InlineData("invalid-external-state")]
    [InlineData("invalid-retention-kind")]
    [InlineData("retention-count-mismatch")]
    [InlineData("local-header-mismatch")]
    [InlineData("forged-crc")]
    [InlineData("database-header")]
    [InlineData("database-trigger")]
    [InlineData("captured-external-raw")]
    [InlineData("captured-sensitive-reservation")]
    [InlineData("captured-sdk-reservation")]
    [InlineData("captured-legacy-blocker")]
    [InlineData("captured-sensitive-complete-orphan")]
    [InlineData("captured-sdk-sealed-orphan")]
    [InlineData("captured-forged-file-terminal")]
    [InlineData("captured-forged-journal-terminal")]
    [InlineData("captured-legacy-no-retention-blocker")]
    [InlineData("captured-legacy-no-retention-blocker-case-alias")]
    [InlineData("oversized-retention-timestamp")]
    [InlineData("oversized-retention-source-id")]
    [InlineData("oversized-legacy-raw-timestamp")]
    [InlineData("malformed-terminal-reservation")]
    [InlineData("oversized-schema-column-name")]
    [InlineData("oversized-trigger-definition")]
    [InlineData("expression-index")]
    [InlineData("partial-index")]
    [InlineData("generated-column")]
    [InlineData("absent-runtime-backup-collision")]
    public void Untrusted_archive_attacks_are_rejected_without_extraction(string attack)
    {
        using var temp = new RuntimeBackupTemp();
        if (attack.StartsWith("captured-legacy-no-retention-blocker", StringComparison.Ordinal) || attack == "oversized-legacy-raw-timestamp")
        {
            temp.CreateDatabaseWithoutRetention("value");
            if (attack == "oversized-legacy-raw-timestamp") temp.InsertUncatalogedRawRecord(temp.DatabasePath);
        }
        else
        {
            temp.CreateDatabase("value");
            if (attack is "oversized-retention-timestamp" or "oversized-retention-source-id") temp.InsertDeletedRawItem(temp.DatabasePath);
            if (attack == "malformed-terminal-reservation") temp.InsertDeletedExternalItem(temp.DatabasePath, "sensitive_bundle");
            if (attack == "captured-forged-file-terminal") temp.InsertDeletedRawItem(temp.DatabasePath);
            if (attack == "captured-forged-journal-terminal")
            {
                temp.InsertExternalReservation(temp.DatabasePath, "sensitive_bundle", "complete");
                temp.InsertDeletedExternalItem(temp.DatabasePath, "sensitive_bundle");
                temp.InsertDeletedRawItem(temp.DatabasePath);
            }
        }
        var valid = Path.Combine(temp.DirectoryPath, "valid.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, valid).Success);
        var malicious = temp.CreateAttackArchive(valid, attack);

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).Inspect(malicious);

        Assert.False(result.Success);
        Assert.Contains(result.ErrorCode, new[]
        {
            RuntimeBackupErrorCodes.ArchiveInvalid,
            RuntimeBackupErrorCodes.CompressionNotAllowed,
            RuntimeBackupErrorCodes.DuplicateEntry,
            RuntimeBackupErrorCodes.UnexpectedEntry,
            RuntimeBackupErrorCodes.ArchiveAttributesInvalid,
            RuntimeBackupErrorCodes.ManifestInvalid,
            RuntimeBackupErrorCodes.RestoreIncompatible,
            RuntimeBackupErrorCodes.ExternalRawStoreActive,
        });
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, "*.restore-stage"));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, ".runtime-backup-inspect-*.sqlite"));
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, ".runtime-backup-inspect-*.sqlite.owner.v1"));
    }

    [Fact]
    public void Startup_recovers_exact_owned_snapshot_and_sidecars_while_preserving_unowned_lookalikes()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var token = new string('a', 32);
        var snapshot = Path.Combine(temp.DirectoryPath, $".{Path.GetFileName(temp.DatabasePath)}.{token}.online-snapshot");
        File.WriteAllText(snapshot, "raw-snapshot");
        File.WriteAllText(snapshot + "-wal", "raw-wal");
        File.WriteAllText(snapshot + "-shm", "raw-shm");
        File.WriteAllText(snapshot + ".owner.v1", "runtime-backup-transient-owner.v1\n");
        var unowned = Path.Combine(temp.DirectoryPath, $".{Path.GetFileName(temp.DatabasePath)}.{new string('b', 32)}.online-snapshot");
        File.WriteAllText(unowned, "operator-lookalike");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).Initialize(temp.DatabasePath);

        Assert.True(result.Success, result.ErrorCode);
        Assert.False(File.Exists(snapshot));
        Assert.False(File.Exists(snapshot + "-wal"));
        Assert.False(File.Exists(snapshot + "-shm"));
        Assert.False(File.Exists(snapshot + ".owner.v1"));
        Assert.Equal("operator-lookalike", File.ReadAllText(unowned));
    }

    [Fact]
    public void Next_publication_recovers_owned_partial_and_inspection_stage_without_deleting_unowned_files()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var outputDirectory = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "runtime-backups")).FullName;
        var partial = Path.Combine(outputDirectory, $".runtime-backup-{new string('c', 32)}.partial");
        var stage = Path.Combine(outputDirectory, $".runtime-backup-inspect-{new string('d', 32)}.sqlite");
        foreach (var raw in new[] { partial, stage })
        {
            File.WriteAllText(raw, "raw-private-bytes");
            File.WriteAllText(raw + ".owner.v1", "runtime-backup-transient-owner.v1\n");
        }
        File.WriteAllText(stage + "-journal", "raw-journal");
        var unrelated = Path.Combine(outputDirectory, "operator.bin");
        File.WriteAllText(unrelated, "keep");
        var output = Path.Combine(outputDirectory, "recovered-output.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, output);

        Assert.True(result.Success, result.ErrorCode);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".owner.v1"));
        Assert.False(File.Exists(stage));
        Assert.False(File.Exists(stage + "-journal"));
        Assert.False(File.Exists(stage + ".owner.v1"));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public void Malformed_or_active_transient_owner_fails_closed_and_preserves_raw_bytes()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var outputDirectory = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "blocked-output")).FullName;
        var malformed = Path.Combine(outputDirectory, $".runtime-backup-{new string('e', 32)}.partial");
        File.WriteAllText(malformed, "raw-malformed-owner");
        File.WriteAllText(malformed + ".owner.v1", "not-an-owner");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);

        var malformedResult = service.CreateAndPublish(temp.DatabasePath, Path.Combine(outputDirectory, "malformed-blocked.zip"));

        Assert.False(malformedResult.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, malformedResult.ErrorCode);
        Assert.Equal("raw-malformed-owner", File.ReadAllText(malformed));

        File.Delete(malformed + ".owner.v1");
        File.Delete(malformed);
        var active = Path.Combine(outputDirectory, $".runtime-backup-{new string('f', 32)}.partial");
        File.WriteAllText(active, "raw-active-owner");
        File.WriteAllText(active + ".owner.v1", "runtime-backup-transient-owner.v1\n");
        using var activeMarker = new FileStream(active + ".owner.v1", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var activeResult = service.CreateAndPublish(temp.DatabasePath, Path.Combine(outputDirectory, "active-blocked.zip"));

        Assert.False(activeResult.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, activeResult.ErrorCode);
        Assert.Equal("raw-active-owner", File.ReadAllText(active));
    }

    [Fact]
    public void Direct_restore_recovers_owned_transients_in_bundle_and_database_directories()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source");
        var bundleDirectory = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "runtime-backups")).FullName;
        var bundle = Path.Combine(bundleDirectory, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, bundle).Success);
        var targetDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"runtime-restore-target-{Guid.NewGuid():N}")).FullName;
        var target = Path.Combine(targetDirectory, "monitor.db");
        var partial = Path.Combine(bundleDirectory, $".runtime-backup-{new string('a', 32)}.partial");
        File.WriteAllText(partial, "private-partial");
        File.WriteAllText(partial + ".owner.v1", "runtime-backup-transient-owner.v1\n");
        var snapshot = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.{new string('b', 32)}.online-snapshot");
        foreach (var path in new[] { snapshot, snapshot + "-wal", snapshot + "-shm" })
            File.WriteAllText(path, "private-snapshot");
        File.WriteAllText(snapshot + ".owner.v1", "runtime-backup-transient-owner.v1\n");

        var result = service.Restore(bundle, target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        foreach (var path in new[] { partial, partial + ".owner.v1", snapshot, snapshot + "-wal", snapshot + "-shm", snapshot + ".owner.v1" })
            Assert.False(File.Exists(path));
        Directory.Delete(targetDirectory, recursive: true);
    }

    [Fact]
    public void Empty_raw_replay_directory_is_backup_compatible_but_any_child_fails_closed()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source");
        var replayDirectory = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "raw-replays")).FullName;
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        var emptyOutput = Path.Combine(temp.DirectoryPath, "empty-raw-replays.zip");

        var empty = service.CreateAndPublish(temp.DatabasePath, emptyOutput);
        File.WriteAllText(Path.Combine(replayDirectory, "capture.bin"), "raw-private");
        var populatedOutput = Path.Combine(temp.DirectoryPath, "populated-raw-replays.zip");
        var populated = service.CreateAndPublish(temp.DatabasePath, populatedOutput);

        Assert.True(empty.Success, empty.ErrorCode);
        Assert.False(populated.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown, populated.ErrorCode);
        Assert.False(File.Exists(populatedOutput));
    }

    [Fact]
    public void Recovery_after_partial_rename_removes_only_inert_owner_marker_and_preserves_published_archive()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var outputDirectory = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "runtime-backups")).FullName;
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        var published = Path.Combine(outputDirectory, "published.zip");
        Assert.True(service.CreateAndPublish(temp.DatabasePath, published).Success);
        var publishedBytes = File.ReadAllBytes(published);
        var movedPartial = Path.Combine(outputDirectory, $".runtime-backup-{new string('1', 32)}.partial");
        File.WriteAllText(movedPartial + ".owner.v1", "runtime-backup-transient-owner.v1\n");

        var next = service.CreateAndPublish(temp.DatabasePath, Path.Combine(outputDirectory, "next.zip"));

        Assert.True(next.Success, next.ErrorCode);
        Assert.False(File.Exists(movedPartial + ".owner.v1"));
        Assert.Equal(publishedBytes, File.ReadAllBytes(published));
    }

    [Theory]
    [InlineData("expression-index")]
    [InlineData("partial-index")]
    [InlineData("generated-column")]
    public void Executable_schema_archive_is_rejected_before_restore_mutates_the_target(string attack)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source");
        var valid = Path.Combine(temp.DirectoryPath, "executable-source.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, valid).Success);
        var malicious = temp.CreateAttackArchive(valid, attack);
        var target = Path.Combine(temp.DirectoryPath, "target.sqlite");
        temp.CreateDatabaseAt(target, "target");
        var before = SHA256.HashData(File.ReadAllBytes(target));

        var result = service.Restore(malicious, target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(target)));
        using var connection = RuntimeBackupTemp.Open(target);
        Assert.Equal("target", RuntimeBackupTemp.ScalarString(connection, "SELECT value FROM runtime_probe;"));
    }

    [Fact]
    public void Absent_runtime_backup_component_collision_is_restore_incompatible_for_preview_and_restore_without_target_mutation()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source");
        var valid = Path.Combine(temp.DirectoryPath, "absent-component-source.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, valid).Success);
        var malicious = temp.CreateAttackArchive(valid, "absent-runtime-backup-collision");
        var target = Path.Combine(temp.DirectoryPath, "absent-component-target.sqlite");
        temp.CreateDatabaseAt(target, "target");
        var before = SHA256.HashData(File.ReadAllBytes(target));

        var preview = service.Preview(malicious, target);
        var restore = service.Restore(malicious, target, new RuntimeRestoreOptions());

        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(target)));
    }

    [Fact]
    public void Malformed_receipt_archive_is_restore_incompatible_for_inspect_preview_and_restore_without_target_mutation()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("source");
        var valid = Path.Combine(temp.DirectoryPath, "receipt-source.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, valid).Success);
        var malicious = temp.CreateAttackArchive(valid, "malformed-runtime-backup-receipt");
        var target = Path.Combine(temp.DirectoryPath, "receipt-target.sqlite");
        temp.CreateDatabaseAt(target, "target");
        var before = SHA256.HashData(File.ReadAllBytes(target));

        var inspection = service.Inspect(malicious);
        var preview = service.Preview(malicious, target);
        var restore = service.Restore(malicious, target, new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(target)));
    }

    [Fact]
    public void Malformed_zip_is_a_fixed_archive_error_for_preview_and_restore_without_target_mutation()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("old");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        var malformed = Path.Combine(temp.DirectoryPath, "malformed.zip");
        File.WriteAllBytes(malformed, [0x50, 0x4b, 0x03, 0x04, 0xff]);
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);

        var preview = service.Preview(malformed, temp.DatabasePath);
        var restore = service.Restore(malformed, temp.DatabasePath, new RuntimeRestoreOptions());

        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ArchiveInvalid, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ArchiveInvalid, restore.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
    }

    [Fact]
    public void Zero_byte_archive_is_invalid_for_inspect_preview_and_restore_without_target_mutation()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("old");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        var empty = Path.Combine(temp.DirectoryPath, "empty.zip");
        File.WriteAllBytes(empty, []);
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);

        var inspection = service.Inspect(empty);
        var preview = service.Preview(empty, temp.DatabasePath);
        var restore = service.Restore(empty, temp.DatabasePath, new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ArchiveInvalid, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ArchiveInvalid, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ArchiveInvalid, restore.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
    }

    [Theory]
    [InlineData("database-header", RuntimeBackupErrorCodes.ArchiveInvalid)]
    [InlineData("database-trigger", RuntimeBackupErrorCodes.RestoreIncompatible)]
    [InlineData("captured-external-raw", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-sensitive-reservation", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-sdk-reservation", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-legacy-blocker", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-sensitive-complete-orphan", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-sdk-sealed-orphan", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-forged-file-terminal", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-forged-journal-terminal", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-legacy-no-retention-blocker", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("captured-legacy-no-retention-blocker-case-alias", RuntimeBackupErrorCodes.ExternalRawStoreActive)]
    [InlineData("oversized-retention-timestamp", RuntimeBackupErrorCodes.RestoreIncompatible)]
    [InlineData("oversized-retention-source-id", RuntimeBackupErrorCodes.RestoreIncompatible)]
    [InlineData("oversized-legacy-raw-timestamp", RuntimeBackupErrorCodes.RestoreIncompatible)]
    [InlineData("malformed-terminal-reservation", RuntimeBackupErrorCodes.RestoreIncompatible)]
    [InlineData("oversized-schema-column-name", RuntimeBackupErrorCodes.RestoreIncompatible)]
    [InlineData("oversized-trigger-definition", RuntimeBackupErrorCodes.RestoreIncompatible)]
    public void Crafted_database_payload_is_rejected_by_preview_and_restore_without_target_mutation(string attack, string expected)
    {
        using var temp = new RuntimeBackupTemp();
        if (attack.StartsWith("captured-legacy-no-retention-blocker", StringComparison.Ordinal) || attack == "oversized-legacy-raw-timestamp")
        {
            temp.CreateDatabaseWithoutRetention("old");
            if (attack == "oversized-legacy-raw-timestamp") temp.InsertUncatalogedRawRecord(temp.DatabasePath);
        }
        else
        {
            temp.CreateDatabase("old");
            if (attack is "oversized-retention-timestamp" or "oversized-retention-source-id") temp.InsertDeletedRawItem(temp.DatabasePath);
            if (attack == "malformed-terminal-reservation") temp.InsertDeletedExternalItem(temp.DatabasePath, "sensitive_bundle");
            if (attack == "captured-forged-file-terminal") temp.InsertDeletedRawItem(temp.DatabasePath);
            if (attack == "captured-forged-journal-terminal")
            {
                temp.InsertExternalReservation(temp.DatabasePath, "sensitive_bundle", "complete");
                temp.InsertDeletedExternalItem(temp.DatabasePath, "sensitive_bundle");
                temp.InsertDeletedRawItem(temp.DatabasePath);
            }
        }
        var valid = Path.Combine(temp.DirectoryPath, "crafted-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, valid).Success);
        var malicious = temp.CreateAttackArchive(valid, attack);
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));

        var preview = service.Preview(malicious, temp.DatabasePath);
        var restore = service.Restore(malicious, temp.DatabasePath, new RuntimeRestoreOptions());

        Assert.False(preview.Success);
        Assert.Equal(expected, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(expected, restore.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
        Assert.False(File.Exists(temp.DatabasePath + ".runtime-restore-journal.json"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.DirectoryPath, ".runtime-restore-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Proposal_apply_private_file_fails_closed_but_empty_default_directory_is_allowed()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var proposal = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "proposal-apply"));
        var backups = Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "runtime-backups"));
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, Path.Combine(backups.FullName, "empty.zip")).Success);
        File.WriteAllText(Path.Combine(proposal.FullName, "apply-root-map.json"),
            "[{\"RootId\":\"11111111-1111-1111-1111-111111111111\",\"Kind\":0,\"CanonicalPath\":\"C:\\\\workspace\"}]");
        Assert.True(service.CreateAndPublish(temp.DatabasePath, Path.Combine(backups.FullName, "configured.zip")).Success);
        File.WriteAllText(Path.Combine(proposal.FullName, "apply-root-map.json"),
            "[{\"RootId\":\"11111111-1111-1111-1111-111111111111\",\"Kind\":0,\"CanonicalPath\":\"C:\\\\workspace\"},{\"RootId\":\"22222222-2222-2222-2222-222222222222\",\"Kind\":0,\"CanonicalPath\":\"C:\\\\workspace\"}]");
        var duplicate = service.CreateAndPublish(temp.DatabasePath, Path.Combine(backups.FullName, "duplicate-root.zip"));
        Assert.False(duplicate.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe, duplicate.ErrorCode);
        File.WriteAllText(Path.Combine(proposal.FullName, "apply-root-map.json"),
            "[{\"RootId\":\"11111111-1111-1111-1111-111111111111\",\"Kind\":0,\"CanonicalPath\":\"C:\\\\workspace\"},{\"RootId\":\"22222222-2222-2222-2222-222222222222\",\"Kind\":0,\"CanonicalPath\":\"C:\\\\WORKSPACE\"}]");
        var caseAlias = service.CreateAndPublish(temp.DatabasePath, Path.Combine(backups.FullName, "case-alias-root.zip"));
        Assert.False(caseAlias.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe, caseAlias.ErrorCode);
        File.Delete(Path.Combine(proposal.FullName, "apply-root-map.json"));
        var draftFile = Path.Combine(proposal.FullName, "private-draft.json");
        File.WriteAllText(draftFile, "private");
        var privateFile = service.CreateAndPublish(temp.DatabasePath, Path.Combine(backups.FullName, "private-file-blocked.zip"));
        Assert.False(privateFile.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateActive, privateFile.ErrorCode);
        File.Delete(draftFile);
        Directory.CreateDirectory(Path.Combine(proposal.FullName, "drafts"));
        File.WriteAllText(Path.Combine(proposal.FullName, "drafts", "private.json"), "private");

        var result = service.CreateAndPublish(temp.DatabasePath, Path.Combine(backups.FullName, "blocked.zip"));

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateActive, result.ErrorCode);
    }

    [Fact]
    public void Unknown_product_owned_runtime_file_fails_closed_without_publication()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        File.WriteAllText(Path.Combine(temp.DirectoryPath, "unknown-runtime-state.bin"), "private");
        var output = Path.Combine(temp.DirectoryPath, "blocked.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, output);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown, result.ErrorCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Distinct_caller_selected_sibling_backups_remain_backupable_without_allowing_unrelated_files()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var first = Path.Combine(temp.DirectoryPath, "operator-first.backup");
        var second = Path.Combine(temp.DirectoryPath, "operator-second.backup");
        var blocked = Path.Combine(temp.DirectoryPath, "operator-third.backup");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);

        var firstResult = service.CreateAndPublish(temp.DatabasePath, first);
        Assert.True(firstResult.Success, firstResult.ErrorCode);
        var firstBytes = File.ReadAllBytes(first);

        var secondResult = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, second);
        Assert.True(secondResult.Success, secondResult.ErrorCode);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));

        File.WriteAllBytes(Path.Combine(temp.DirectoryPath, "unrelated.bin"), [1, 2, 3]);
        var blockedResult = service.CreateAndPublish(temp.DatabasePath, blocked);
        Assert.False(blockedResult.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown, blockedResult.ErrorCode);
        Assert.False(File.Exists(blocked));
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("manifest")]
    public void Tampered_sibling_envelope_or_manifest_is_not_treated_as_an_owned_backup(string attack)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var first = Path.Combine(temp.DirectoryPath, "operator-first.backup");
        var second = Path.Combine(temp.DirectoryPath, "operator-second.backup");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, first).Success);
        if (attack == "envelope") File.AppendAllText(first, "tampered");
        else
        {
            _ = temp.CreateAttackArchive(first, "duplicate-manifest-key");
            File.Delete(first);
        }

        var result = service.CreateAndPublish(temp.DatabasePath, second);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown, result.ErrorCode);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void Runtime_backup_component_migrates_from_absent_to_v1_with_sanitized_exact_shape()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).Initialize(temp.DatabasePath);

        Assert.True(result.Success, result.ErrorCode);
        using var connection = RuntimeBackupTemp.Open(temp.DatabasePath);
        Assert.Equal(1L, RuntimeBackupTemp.Scalar(connection, "SELECT version FROM schema_version WHERE component='runtime_backup';"));
        Assert.Equal(
            ["operation_id", "operation_kind", "artifact_sha256", "result_code", "occurred_at", "reintroduction_count", "pre_restore_backup_created"],
            RuntimeBackupTemp.Strings(connection, "SELECT name FROM pragma_table_info('runtime_backup_receipts') ORDER BY cid;"));
        Assert.DoesNotContain(RuntimeBackupTemp.Strings(connection, "SELECT name FROM pragma_table_info('runtime_backup_receipts') ORDER BY cid;"),
            name => name.Contains("path", StringComparison.OrdinalIgnoreCase) || name.Contains("raw", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["runtime_backup_receipts_no_delete", "runtime_backup_receipts_no_replace", "runtime_backup_receipts_no_update"],
            RuntimeBackupTemp.Strings(connection, "SELECT name FROM sqlite_schema WHERE type='trigger' AND tbl_name='runtime_backup_receipts' ORDER BY name;"));
        var operationId = Guid.CreateVersion7(temp.TimeProvider.GetUtcNow()).ToString("D");
        RuntimeBackupTemp.Execute(connection,
            $"INSERT INTO runtime_backup_receipts VALUES('{operationId}','backup','{new string('1', 64)}','backup_succeeded','2026-07-23T01:02:03.0000000+00:00',0,0);");
        Assert.Throws<SqliteException>(() => RuntimeBackupTemp.Execute(connection, "UPDATE runtime_backup_receipts SET reintroduction_count=1;"));
        Assert.Throws<SqliteException>(() => RuntimeBackupTemp.Execute(connection, "DELETE FROM runtime_backup_receipts;"));
        Assert.Throws<SqliteException>(() => RuntimeBackupTemp.Execute(connection,
            $"INSERT OR REPLACE INTO runtime_backup_receipts VALUES('{operationId}','backup','{new string('2', 64)}','backup_succeeded','2026-07-23T01:02:03.0000000+00:00',0,0);"));
        Assert.Equal(new string('1', 64), RuntimeBackupTemp.ScalarString(connection, "SELECT artifact_sha256 FROM runtime_backup_receipts;"));
    }

    [Theory]
    [InlineData("uuid-v4")]
    [InlineData("digest-uppercase")]
    [InlineData("time-invalid-date")]
    [InlineData("count-negative")]
    [InlineData("boolean-invalid")]
    [InlineData("cross-field")]
    [InlineData("blob-kind")]
    public void Runtime_backup_receipt_ddl_rejects_invalid_rows_without_disabling_checks(string attack)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        Assert.True(new SqliteRuntimeBackupService(temp.TimeProvider).Initialize(temp.DatabasePath).Success);
        using var connection = RuntimeBackupTemp.Open(temp.DatabasePath);
        object operationId = Guid.CreateVersion7(temp.TimeProvider.GetUtcNow()).ToString("D");
        object operationKind = "backup";
        object digest = new string('1', 64);
        object resultCode = "backup_succeeded";
        object occurredAt = "2026-07-23T01:02:03.0000000+00:00";
        object count = 0;
        object preRestore = 0;
        switch (attack)
        {
            case "uuid-v4": operationId = Guid.NewGuid().ToString("D"); break;
            case "digest-uppercase": digest = new string('A', 64); break;
            case "time-invalid-date": occurredAt = "2026-02-30T01:02:03.0000000+00:00"; break;
            case "count-negative": count = -1; break;
            case "boolean-invalid": preRestore = 2; break;
            case "cross-field": count = 1; break;
            case "blob-kind": operationKind = Encoding.UTF8.GetBytes("backup"); break;
            default: throw new InvalidOperationException(attack);
        }
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO runtime_backup_receipts(operation_id,operation_kind,artifact_sha256,result_code,occurred_at,reintroduction_count,pre_restore_backup_created) VALUES($id,$kind,$digest,$code,$at,$count,$pre);";
        insert.Parameters.AddWithValue("$id", operationId);
        insert.Parameters.AddWithValue("$kind", operationKind);
        insert.Parameters.AddWithValue("$digest", digest);
        insert.Parameters.AddWithValue("$code", resultCode);
        insert.Parameters.AddWithValue("$at", occurredAt);
        insert.Parameters.AddWithValue("$count", count);
        insert.Parameters.AddWithValue("$pre", preRestore);

        Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery());
        Assert.Equal(0L, RuntimeBackupTemp.Scalar(connection, "SELECT COUNT(*) FROM runtime_backup_receipts;"));
    }

    [Theory]
    [InlineData("uuid-v4")]
    [InlineData("uuid-uppercase")]
    [InlineData("uuid-blob")]
    [InlineData("digest-uppercase")]
    [InlineData("digest-blob")]
    [InlineData("kind-blob")]
    [InlineData("time-z")]
    [InlineData("time-invalid-date")]
    [InlineData("time-blob")]
    [InlineData("count-negative")]
    [InlineData("count-overflow")]
    [InlineData("count-real")]
    [InlineData("boolean-invalid")]
    [InlineData("boolean-blob")]
    [InlineData("backup-result-mismatch")]
    [InlineData("backup-count-mismatch")]
    [InlineData("backup-pre-restore-mismatch")]
    [InlineData("restore-result-mismatch")]
    public void Runtime_backup_receipt_rows_require_uuid_v7_exact_utc_types_bounds_and_cross_field_consistency(string attack)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.Initialize(temp.DatabasePath).Success);
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            object operationId = Guid.CreateVersion7(temp.TimeProvider.GetUtcNow()).ToString("D");
            object operationKind = "backup";
            object digest = new string('1', 64);
            object resultCode = "backup_succeeded";
            object occurredAt = "2026-07-23T01:02:03.0000000+00:00";
            object count = 0;
            object preRestore = 0;
            switch (attack)
            {
                case "uuid-v4": operationId = Guid.NewGuid().ToString("D"); break;
                case "uuid-uppercase": operationId = ((string)operationId).ToUpperInvariant(); break;
                case "uuid-blob": operationId = Encoding.UTF8.GetBytes((string)operationId); break;
                case "digest-uppercase": digest = new string('A', 64); break;
                case "digest-blob": digest = SHA256.HashData([1]); break;
                case "kind-blob": operationKind = Encoding.UTF8.GetBytes("backup"); break;
                case "time-z": occurredAt = "2026-07-23T01:02:03.0000000Z"; break;
                case "time-invalid-date": occurredAt = "2026-02-30T01:02:03.0000000+00:00"; break;
                case "time-blob": occurredAt = Encoding.UTF8.GetBytes((string)occurredAt); break;
                case "count-negative": count = -1; break;
                case "count-overflow": count = (long)int.MaxValue + 1; break;
                case "count-real": count = 0.5; break;
                case "boolean-invalid": preRestore = 2; break;
                case "boolean-blob": preRestore = new byte[] { 0 }; break;
                case "backup-result-mismatch": resultCode = "restore_succeeded"; break;
                case "backup-count-mismatch": count = 1; break;
                case "backup-pre-restore-mismatch": preRestore = 1; break;
                case "restore-result-mismatch": operationKind = "restore"; break;
                default: throw new InvalidOperationException(attack);
            }

            RuntimeBackupTemp.Execute(connection, "PRAGMA ignore_check_constraints=ON;");
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO runtime_backup_receipts(operation_id,operation_kind,artifact_sha256,result_code,occurred_at,reintroduction_count,pre_restore_backup_created) VALUES($id,$kind,$digest,$code,$at,$count,$pre);";
            insert.Parameters.AddWithValue("$id", operationId);
            insert.Parameters.AddWithValue("$kind", operationKind);
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$code", resultCode);
            insert.Parameters.AddWithValue("$at", occurredAt);
            insert.Parameters.AddWithValue("$count", count);
            insert.Parameters.AddWithValue("$pre", preRestore);
            Assert.Equal(1, insert.ExecuteNonQuery());
            RuntimeBackupTemp.Execute(connection, "PRAGMA ignore_check_constraints=OFF; PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));

        var preflight = service.PreflightForMigration(temp.DatabasePath);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
    }

    [Fact]
    public void Malformed_runtime_backup_v1_shape_is_rejected_without_mutating_candidate_bytes()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, "INSERT INTO schema_version(component,version) VALUES('runtime_backup',1);");
            RuntimeBackupTemp.Execute(connection, "CREATE TABLE runtime_backup_receipts(a,b,c,d,e,f,g);");
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));
        var preflight = new SqliteRuntimeBackupService(temp.TimeProvider).PreflightForMigration(temp.DatabasePath);

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).Initialize(temp.DatabasePath);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
    }

    [Theory]
    [InlineData("runtime_backup", "CREATE TABLE RUNTIME_BACKUP_RECEIPTS(bad TEXT);")]
    [InlineData("runtime_backup", "CREATE TABLE runtime_backup_unowned_collision(bad TEXT);")]
    [InlineData("doctor", "CREATE TABLE doctor_unowned_collision(bad TEXT);")]
    [InlineData("alert_engine", "CREATE TABLE alert_evaluations(bad TEXT);")]
    [InlineData("alert_engine", "CREATE TABLE alert_unowned_collision(bad TEXT);")]
    [InlineData("alert_lifecycle", "CREATE TABLE alert_lifecycle_events(bad TEXT);")]
    [InlineData("first_trace_navigation", "CREATE TABLE first_trace_evidence_navigation(bad TEXT);")]
    [InlineData("historical_instruction_analysis", "CREATE TABLE HISTORICAL_INSTRUCTION_ANALYSIS_RUNS(bad TEXT);")]
    [InlineData("historical_import", "CREATE TABLE historical_import_unowned_collision(bad TEXT);")]
    [InlineData("sanitized_import", "CREATE TABLE sanitized_import_unowned_collision(bad TEXT);")]
    public void Absent_component_namespace_collision_is_rejected_by_read_only_preflight(
        string component,
        string collisionSql)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, collisionSql);
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).PreflightForMigration(temp.DatabasePath);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
        using var verify = RuntimeBackupTemp.Open(temp.DatabasePath);
        Assert.Equal(0L, RuntimeBackupTemp.Scalar(verify, $"SELECT COUNT(*) FROM schema_version WHERE component='{component}';"));
    }

    [Fact]
    public void Declared_runtime_backup_component_rejects_extra_owned_namespace_objects()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.Initialize(temp.DatabasePath).Success);
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, "CREATE TABLE runtime_backup_extra_private_state(value TEXT);");
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));

        var result = service.PreflightForMigration(temp.DatabasePath);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
    }

    [Theory]
    [InlineData("generated-table")]
    [InlineData("expression-index")]
    public void Writable_schema_cannot_hide_executable_objects_under_sqlite_reserved_names(string attack)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            if (attack == "expression-index")
                RuntimeBackupTemp.Execute(connection, "CREATE INDEX runtime_probe_expression_index ON runtime_probe(lower(value));");
            RuntimeBackupTemp.Execute(connection, "PRAGMA writable_schema=ON;");
            if (attack == "generated-table")
                RuntimeBackupTemp.Execute(connection, "UPDATE sqlite_schema SET name='sqlite_runtime_probe',tbl_name='sqlite_runtime_probe',sql='CREATE TABLE sqlite_runtime_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL,derived TEXT GENERATED ALWAYS AS (value) VIRTUAL)' WHERE type='table' AND name='runtime_probe';");
            else
                RuntimeBackupTemp.Execute(connection, "UPDATE sqlite_schema SET name='sqlite_runtime_probe_expression_index',sql='CREATE INDEX sqlite_runtime_probe_expression_index ON runtime_probe(lower(value))' WHERE type='index' AND name='runtime_probe_expression_index';");
            RuntimeBackupTemp.Execute(connection, "PRAGMA writable_schema=OFF; PRAGMA schema_version=999; PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).PreflightForMigration(temp.DatabasePath);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
    }

    [Fact]
    public void Oversized_source_metadata_fails_read_only_preflight_before_initialize_or_backup_mutates_the_database()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabaseWithoutRetention("value");
        temp.InsertUncatalogedRawRecord(temp.DatabasePath);
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE raw_records SET received_at=$value;";
            command.Parameters.AddWithValue("$value", new string('x', RuntimeBackupLimits.MaximumRetentionPreflightTextBytes + 1));
            command.ExecuteNonQuery();
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));
        var output = Path.Combine(temp.DirectoryPath, "oversized-source.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);

        var preflight = service.PreflightForMigration(temp.DatabasePath);
        var initialize = service.Initialize(temp.DatabasePath);
        var create = service.CreateAndPublish(temp.DatabasePath, output);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(initialize.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, initialize.ErrorCode);
        Assert.False(create.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, create.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Case_aliased_retention_table_cannot_bypass_value_bounds_before_initialization()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabaseWithoutRetention("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, "CREATE TABLE RETENTION_STORE_INSTANCES(id INTEGER PRIMARY KEY,store_instance_id TEXT NOT NULL UNIQUE);");
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO RETENTION_STORE_INSTANCES(id,store_instance_id) VALUES(1,$value);";
            command.Parameters.AddWithValue("$value", new string('x', RuntimeBackupLimits.MaximumRetentionPreflightTextBytes + 1));
            command.ExecuteNonQuery();
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);

        var preflight = service.PreflightForMigration(temp.DatabasePath);
        var initialize = service.Initialize(temp.DatabasePath);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(initialize.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, initialize.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
    }

    [Theory]
    [InlineData("view")]
    [InlineData("virtual")]
    [InlineData("trigger")]
    public void Executable_database_objects_outside_the_exact_allowlist_are_rejected_before_publication(string kind)
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, kind switch
            {
                "view" => "CREATE VIEW private_projection AS SELECT value FROM runtime_probe;",
                "virtual" => "CREATE VIRTUAL TABLE sqliteevil USING fts5(value);",
                "trigger" => "CREATE TRIGGER private_trigger AFTER INSERT ON runtime_probe BEGIN DELETE FROM runtime_probe; END;",
                _ => throw new InvalidOperationException(),
            });
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var output = Path.Combine(temp.DirectoryPath, $"blocked-{kind}.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, output);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Wrong_storage_class_in_component_inventory_returns_fixed_error_without_mutation()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, "UPDATE schema_version SET component=X'80' WHERE component='monitor';");
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.DatabasePath));
        var output = Path.Combine(temp.DirectoryPath, "wrong-component-type.zip");

        var preflight = new SqliteRuntimeBackupService(temp.TimeProvider).PreflightForMigration(temp.DatabasePath);
        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, output);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.DatabasePath)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Database_component_inventory_is_bounded_while_reading()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, string.Join('\n', Enumerable.Range(0, RuntimeBackupLimits.MaximumInventoryItems)
                .Select(index => $"INSERT INTO schema_version(component,version) VALUES('extra_{index:D3}',1);")));
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).PreflightForMigration(temp.DatabasePath);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Fact]
    public void Database_table_inventory_is_bounded_before_manifest_allocation()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, string.Join('\n', Enumerable.Range(0, RuntimeBackupLimits.MaximumInventoryItems)
                .Select(index => $"CREATE TABLE extra_{index:D3}(id INTEGER);")));
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var output = Path.Combine(temp.DirectoryPath, "too-many-tables.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, output);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Database_projection_cursor_inventory_is_bounded_while_reading()
    {
        using var temp = new RuntimeBackupTemp();
        temp.CreateDatabase("value");
        using (var connection = RuntimeBackupTemp.Open(temp.DatabasePath))
        {
            RuntimeBackupTemp.Execute(connection, "CREATE TABLE session_projection_state(projector_key TEXT PRIMARY KEY,projection_cursor INTEGER NULL);");
            RuntimeBackupTemp.Execute(connection, string.Join('\n', Enumerable.Range(0, RuntimeBackupLimits.MaximumInventoryItems + 1)
                .Select(index => $"INSERT INTO session_projection_state(projector_key,projection_cursor) VALUES('projector_{index:D3}',{index});")));
            RuntimeBackupTemp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var output = Path.Combine(temp.DirectoryPath, "too-many-cursors.zip");

        var result = new SqliteRuntimeBackupService(temp.TimeProvider).CreateAndPublish(temp.DatabasePath, output);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.ManifestInvalid, result.ErrorCode);
        Assert.False(File.Exists(output));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private sealed class RuntimeBackupTemp : IDisposable
    {
        internal RuntimeBackupTemp()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"runtime-backup-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "monitor.db");
            TimeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero));
        }

        internal string DirectoryPath { get; }
        internal string DatabasePath { get; }
        internal TimeProvider TimeProvider { get; }

        internal void CreateDatabase(string value)
        {
            CreateDatabaseAt(DatabasePath, value);
        }

        internal void CreateDatabaseAt(string path, string value)
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using (var transaction = connection.BeginTransaction()) { MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction); transaction.Commit(); }
            using (var transaction = connection.BeginTransaction()) { RetentionSchemaMigrator.Apply(connection, transaction); transaction.Commit(); }
            Execute(connection, $"UPDATE retention_store_instances SET store_instance_id='{new string('1', 32)}' WHERE id=1;");
            Execute(connection, "CREATE TABLE runtime_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL);");
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO runtime_probe(id,value) VALUES(1,$value);";
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }

        internal void CreateDatabaseWithoutRetention(string value)
        {
            using var connection = Open(DatabasePath);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using (var transaction = connection.BeginTransaction()) { MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction); transaction.Commit(); }
            Execute(connection, "CREATE TABLE runtime_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL);");
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO runtime_probe(id,value) VALUES(1,$value);";
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }

        internal void InsertRetentionItem(string kind, string state, bool readDenied, string sourceItemId)
        {
            using var connection = Open(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,adapter_coverage_version) VALUES($id,$store,$kind,$source,1,$receipt,$captured,$expires,$policy,1,$state,1,$denied,NULL,1);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$store", new string('1', 32));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$source", sourceItemId);
            command.Parameters.AddWithValue("$receipt", SHA256.HashData([1]));
            command.Parameters.AddWithValue("$captured", "2026-01-01T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$expires", "2026-04-01T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$policy", kind == "sensitive_bundle" ? "sensitive-bundle-7d" : "raw-default-90d");
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$denied", readDenied ? "2026-04-01T00:00:00.0000000+00:00" : DBNull.Value);
            command.ExecuteNonQuery();
        }

        internal void InsertExternalReservation(string path, string kind, string? phase = null)
        {
            using var connection = Open(path);
            var reserved = "2026-01-01T00:00:00.0000000+00:00";
            var ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime.Ticks;
            var capture = new string(kind == "sensitive_bundle" ? 'b' : 'c', 32);
            var parent = Path.Combine(DirectoryPath, "external-parent");
            using var command = connection.CreateCommand();
            if (kind == "sensitive_bundle")
            {
                command.CommandText = "INSERT INTO retention_file_capture_reservations(capture_id,store_instance_id,store_kind,source_item_id,reserved_at,reserved_at_utc_ticks,policy_id,policy_version,parent_locator,staging_locator,final_locator,owner_token,marker_sha256,manifest_sha256,phase,durable_cursor,planned_member_count,planned_total_bytes,error_code,updated_at,legacy_v1) VALUES($capture,$store,'sensitive_bundle',$capture,$reserved,$ticks,'sensitive-bundle-7d',1,$parent,$staging,$final,$token,NULL,NULL,$phase,NULL,0,0,NULL,$reserved,0);";
                command.Parameters.AddWithValue("$staging", Path.Combine(parent, $".{capture}.staging"));
                command.Parameters.AddWithValue("$final", Path.Combine(parent, capture));
            }
            else
            {
                command.CommandText = "INSERT INTO retention_analysis_sdk_directory_reservations(capture_id,analysis_run_id,store_instance_id,requested_at,requested_at_utc_ticks,parent_locator,child_locator,analysis_owner_token_sha256,owner_token,marker_sha256,phase,error_code,revision,updated_at) VALUES($capture,1,$store,$reserved,$ticks,$parent,$child,$run_token,$token,$marker,$phase,NULL,1,$reserved);";
                command.Parameters.AddWithValue("$child", Path.Combine(parent, capture));
                command.Parameters.AddWithValue("$run_token", SHA256.HashData([1]));
                command.Parameters.AddWithValue("$marker", SHA256.HashData([2]));
            }
            command.Parameters.AddWithValue("$capture", capture);
            command.Parameters.AddWithValue("$store", new string('1', 32));
            command.Parameters.AddWithValue("$reserved", reserved);
            command.Parameters.AddWithValue("$ticks", ticks);
            command.Parameters.AddWithValue("$parent", parent);
            command.Parameters.AddWithValue("$token", SHA256.HashData([3]));
            command.Parameters.AddWithValue("$phase", phase ?? "reserved");
            command.ExecuteNonQuery();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void InsertLegacyBundleBlocker(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO retention_legacy_bundle_blockers(root_locator,classification,recorded_at) VALUES($root,'legacy_bundle_unverifiable','2026-01-01T00:00:00.0000000+00:00');";
            command.Parameters.AddWithValue("$root", Path.Combine(DirectoryPath, "legacy-external-root"));
            command.ExecuteNonQuery();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void InsertDeletedExternalItem(string path, string kind)
        {
            using var connection = Open(path);
            var source = new string(kind == "sensitive_bundle" ? 'b' : 'c', 32);
            var itemId = new string(kind == "sensitive_bundle" ? 'd' : 'e', 32);
            using (var item = connection.CreateCommand())
            {
                item.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,private_locator,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,deleted_at,adapter_coverage_version) VALUES($item,$store,$kind,$source,1,zeroblob(32),$locator,'2026-01-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00',$policy,1,'deleted',2,'2026-04-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',1);";
                item.Parameters.AddWithValue("$item", itemId);
                item.Parameters.AddWithValue("$store", new string('1', 32));
                item.Parameters.AddWithValue("$kind", kind);
                item.Parameters.AddWithValue("$source", source);
                item.Parameters.AddWithValue("$locator", Path.Combine(DirectoryPath, "external-parent", source));
                item.Parameters.AddWithValue("$policy", kind == "sensitive_bundle" ? "sensitive-bundle-7d" : "raw-default-90d");
                item.ExecuteNonQuery();
            }
            using (var tombstone = connection.CreateCommand())
            {
                tombstone.CommandText = "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES($item,'2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00');";
                tombstone.Parameters.AddWithValue("$item", itemId);
                tombstone.ExecuteNonQuery();
            }
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void InsertDeletedRawItem(string path)
        {
            using var connection = Open(path);
            var itemId = new string('f', 32);
            using (var item = connection.CreateCommand())
            {
                item.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,deleted_at,adapter_coverage_version) VALUES($item,$store,'raw_record','999',1,zeroblob(32),'2026-01-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','raw-default-90d',1,'deleted',2,'2026-04-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',1);";
                item.Parameters.AddWithValue("$item", itemId);
                item.Parameters.AddWithValue("$store", new string('1', 32));
                item.ExecuteNonQuery();
            }
            using (var tombstone = connection.CreateCommand())
            {
                tombstone.CommandText = "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES($item,'2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00');";
                tombstone.Parameters.AddWithValue("$item", itemId);
                tombstone.ExecuteNonQuery();
            }
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void InsertUncatalogedRawRecord(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token) VALUES(1,'raw-otlp',NULL,'2026-01-01T00:00:00.0000000+00:00','{}','{}',1,$token);";
            command.Parameters.AddWithValue("$token", SHA256.HashData([7]));
            command.ExecuteNonQuery();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal string CreateAttackArchive(string valid, string attack)
        {
            var output = Path.Combine(DirectoryPath, $"{attack}.zip");
            if (attack == "corrupt")
            {
                var bytes = File.ReadAllBytes(valid);
                bytes[^1] ^= 0xff;
                File.WriteAllBytes(output, bytes);
                return output;
            }
            if (attack == "local-header-mismatch")
            {
                var bytes = File.ReadAllBytes(valid);
                bytes[10] ^= 1;
                File.WriteAllBytes(output, bytes);
                return output;
            }
            if (attack == "forged-crc")
            {
                var bytes = File.ReadAllBytes(valid);
                var local = FindSignature(bytes, 0x04034b50u);
                var central = FindSignature(bytes, 0x02014b50u);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 14, 4), 0xdeadbeefu);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 16, 4), 0xdeadbeefu);
                File.WriteAllBytes(output, bytes);
                return output;
            }

            using var source = ZipFile.OpenRead(valid);
            var manifest = Read(source.GetEntry("manifest.json")!);
            var database = Read(source.GetEntry("database.sqlite")!);
            if (attack == "database-header")
            {
                database[0] ^= 0xff;
                manifest = ReplaceDatabaseHash(manifest, database);
            }
            if (attack is "database-trigger" or "captured-external-raw" or "captured-sensitive-reservation" or "captured-sdk-reservation" or "captured-legacy-blocker" or "captured-sensitive-complete-orphan" or "captured-sdk-sealed-orphan" or "captured-forged-file-terminal" or "captured-forged-journal-terminal" or "captured-legacy-no-retention-blocker" or "captured-legacy-no-retention-blocker-case-alias" or "oversized-retention-timestamp" or "oversized-retention-source-id" or "oversized-legacy-raw-timestamp" or "malformed-terminal-reservation" or "oversized-schema-column-name" or "oversized-trigger-definition" or "expression-index" or "partial-index" or "generated-column" or "absent-runtime-backup-collision" or "malformed-runtime-backup-receipt")
            {
                var mutatedPath = Path.Combine(DirectoryPath, $".{attack}-{Guid.NewGuid():N}.sqlite");
                File.WriteAllBytes(mutatedPath, database);
                using (var connection = Open(mutatedPath))
                {
                    if (attack == "database-trigger")
                        Execute(connection, "CREATE TRIGGER private_bundle_trigger AFTER INSERT ON runtime_probe BEGIN DELETE FROM runtime_probe; END;");
                    else if (attack == "expression-index")
                        Execute(connection, "CREATE INDEX runtime_probe_expression_index ON runtime_probe(lower(value));");
                    else if (attack == "partial-index")
                        Execute(connection, "CREATE INDEX runtime_probe_partial_index ON runtime_probe(value) WHERE value='source-value';");
                    else if (attack == "generated-column")
                        Execute(connection, "ALTER TABLE runtime_probe ADD COLUMN generated_value TEXT GENERATED ALWAYS AS (value || '-generated') VIRTUAL;");
                    else if (attack == "absent-runtime-backup-collision")
                        Execute(connection, "DELETE FROM schema_version WHERE component='runtime_backup';");
                    else if (attack == "malformed-runtime-backup-receipt")
                    {
                        Execute(connection, "PRAGMA ignore_check_constraints=ON;");
                        Execute(connection, $"INSERT INTO runtime_backup_receipts VALUES('{Guid.CreateVersion7():D}','backup','{new string('1', 64)}','backup_succeeded','2026-02-30T01:02:03.0000000+00:00',0,0);");
                        Execute(connection, "PRAGMA ignore_check_constraints=OFF;");
                    }
                    else if (attack is "captured-legacy-no-retention-blocker" or "captured-legacy-no-retention-blocker-case-alias")
                    {
                        var table = attack.EndsWith("case-alias", StringComparison.Ordinal)
                            ? "RETENTION_LEGACY_BUNDLE_BLOCKERS"
                            : "retention_legacy_bundle_blockers";
                        Execute(connection, $"CREATE TABLE \"{table}\" (root_locator TEXT PRIMARY KEY, classification TEXT NOT NULL CHECK(classification='legacy_bundle_unverifiable'), recorded_at TEXT NOT NULL);");
                        using var blocker = connection.CreateCommand();
                        blocker.CommandText = $"INSERT INTO \"{table}\"(root_locator,classification,recorded_at) VALUES($root,'legacy_bundle_unverifiable','2026-01-01T00:00:00.0000000+00:00');";
                        blocker.Parameters.AddWithValue("$root", Path.Combine(DirectoryPath, "legacy-no-retention-root"));
                        blocker.ExecuteNonQuery();
                    }
                    else if (attack is "oversized-retention-timestamp" or "oversized-retention-source-id")
                    {
                        using var oversized = connection.CreateCommand();
                        oversized.CommandText = attack == "oversized-retention-timestamp"
                            ? "UPDATE retention_items SET captured_at=$value;"
                            : "UPDATE retention_items SET source_item_id=$value;";
                        oversized.Parameters.AddWithValue("$value", new string('x', RuntimeBackupLimits.MaximumRetentionPreflightTextBytes + 1));
                        oversized.ExecuteNonQuery();
                    }
                    else if (attack == "oversized-legacy-raw-timestamp")
                    {
                        using var oversized = connection.CreateCommand();
                        oversized.CommandText = "UPDATE raw_records SET received_at=$value;";
                        oversized.Parameters.AddWithValue("$value", new string('x', RuntimeBackupLimits.MaximumRetentionPreflightTextBytes + 1));
                        oversized.ExecuteNonQuery();
                    }
                    else if (attack == "malformed-terminal-reservation")
                    {
                        Execute(connection, "DROP TABLE retention_file_capture_reservations;");
                        Execute(connection, "CREATE TABLE retention_file_capture_reservations(capture_id TEXT PRIMARY KEY,store_instance_id TEXT NOT NULL,store_kind TEXT NOT NULL,source_item_id TEXT NOT NULL,phase TEXT NOT NULL);");
                        Execute(connection, $"INSERT INTO retention_file_capture_reservations(capture_id,store_instance_id,store_kind,source_item_id,phase) VALUES('{new string('b', 32)}','{new string('1', 32)}','sensitive_bundle','{new string('b', 32)}','complete');");
                    }
                    else if (attack == "captured-forged-file-terminal")
                    {
                        Execute(connection, "DROP TABLE retention_file_capture_reservations;");
                        Execute(connection, "CREATE TABLE retention_file_capture_reservations(capture_id TEXT PRIMARY KEY,store_instance_id TEXT,store_kind TEXT,source_item_id TEXT,reserved_at TEXT,reserved_at_utc_ticks INTEGER,policy_id TEXT,policy_version INTEGER,parent_locator TEXT,staging_locator TEXT,final_locator TEXT,owner_token BLOB,marker_sha256 BLOB,manifest_sha256 BLOB,phase TEXT,durable_cursor INTEGER,planned_member_count INTEGER,planned_total_bytes INTEGER,error_code TEXT,updated_at TEXT,legacy_v1 INTEGER);");
                        Execute(connection, $"INSERT INTO retention_file_capture_reservations(capture_id,store_instance_id,store_kind,source_item_id,phase) VALUES('{new string('b', 32)}','{new string('1', 32)}','raw_record','999','complete');");
                    }
                    else if (attack == "captured-forged-journal-terminal")
                        Execute(connection, $"INSERT INTO retention_capture_journal(item_id,phase,durable_cursor) VALUES('{new string('f', 32)}','complete',NULL);");
                    else if (attack == "oversized-schema-column-name")
                    {
                        var column = new string('z', RuntimeBackupLimits.MaximumSchemaIdentifierBytes + 1);
                        Execute(connection, $"ALTER TABLE retention_items ADD COLUMN \"{column}\" TEXT;");
                    }
                    else if (attack == "oversized-trigger-definition")
                    {
                        Execute(connection, "DROP TRIGGER retention_raw_records_token_immutable;");
                        var padding = new string(' ', RuntimeBackupLimits.MaximumSchemaDefinitionBytes + 1);
                        Execute(connection, $"CREATE TRIGGER retention_raw_records_token_immutable {padding} BEFORE UPDATE OF retention_owner_token ON raw_records WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;");
                    }
                    else if (attack == "captured-external-raw")
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES($id,$store,'sensitive_bundle',$source,1,$receipt,'2026-01-01T00:00:00.0000000+00:00','2026-01-08T00:00:00.0000000+00:00','sensitive-bundle-7d',1,'expiring',1,1);";
                        command.Parameters.AddWithValue("$id", new string('b', 32));
                        command.Parameters.AddWithValue("$store", new string('1', 32));
                        command.Parameters.AddWithValue("$source", new string('c', 32));
                        command.Parameters.AddWithValue("$receipt", SHA256.HashData([9]));
                        command.ExecuteNonQuery();
                    }
                    else if (attack == "captured-legacy-blocker") InsertLegacyBundleBlocker(mutatedPath);
                    else
                    {
                        var sensitive = attack is "captured-sensitive-reservation" or "captured-sensitive-complete-orphan";
                        var phase = attack switch
                        {
                            "captured-sensitive-complete-orphan" => "complete",
                            "captured-sdk-sealed-orphan" => "sealed",
                            _ => null,
                        };
                        InsertExternalReservation(mutatedPath, sensitive ? "sensitive_bundle" : "analysis_sdk_directory", phase);
                    }
                    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                }
                database = File.ReadAllBytes(mutatedPath);
                File.Delete(mutatedPath);
                manifest = ReplaceDatabaseHash(manifest, database);
                if (attack == "absent-runtime-backup-collision")
                {
                    var parsed = RuntimeBackupJson.ParseManifest(manifest);
                    var versions = parsed.ComponentVersions.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                    Assert.True(versions.Remove("runtime_backup"));
                    var rows = parsed.RowCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                    rows["schema_version"]--;
                    manifest = RuntimeBackupJson.WriteManifest(parsed with { ComponentVersions = versions, RowCounts = rows });
                }
                if (attack == "malformed-runtime-backup-receipt")
                {
                    var parsed = RuntimeBackupJson.ParseManifest(manifest);
                    var rows = parsed.RowCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                    rows["runtime_backup_receipts"]++;
                    manifest = RuntimeBackupJson.WriteManifest(parsed with { RowCounts = rows });
                }
                if (attack is "captured-legacy-no-retention-blocker" or "captured-legacy-no-retention-blocker-case-alias")
                {
                    var table = attack.EndsWith("case-alias", StringComparison.Ordinal)
                        ? "RETENTION_LEGACY_BUNDLE_BLOCKERS"
                        : "retention_legacy_bundle_blockers";
                    var parsed = RuntimeBackupJson.ParseManifest(manifest);
                    var rows = parsed.RowCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                    rows.Add(table, 1);
                    manifest = RuntimeBackupJson.WriteManifest(parsed with { RowCounts = rows });
                }
                if (attack == "captured-external-raw")
                {
                    var json = Encoding.UTF8.GetString(manifest);
                    json = ReplaceRequired(json, "\"retention_items\":0", "\"retention_items\":1");
                    json = ReplaceRequired(json, "\"sensitive_bundle\":0", "\"sensitive_bundle\":1");
                    json = ReplaceRequired(json, "\"expiring\":0", "\"expiring\":1");
                    json = ReplaceRequired(json, "\"earliest_captured_at\":null", "\"earliest_captured_at\":\"2026-01-01T00:00:00.0000000\\u002B00:00\"");
                    json = ReplaceRequired(json, "\"latest_captured_at\":null", "\"latest_captured_at\":\"2026-01-01T00:00:00.0000000\\u002B00:00\"");
                    json = ReplaceRequired(json, "\"earliest_expires_at\":null", "\"earliest_expires_at\":\"2026-01-08T00:00:00.0000000\\u002B00:00\"");
                    json = ReplaceRequired(json, "\"latest_expires_at\":null", "\"latest_expires_at\":\"2026-01-08T00:00:00.0000000\\u002B00:00\"");
                    json = ReplaceRequired(json, "\"policies\":[]", "\"policies\":[\"sensitive-bundle-7d@1\"]");
                    manifest = Encoding.UTF8.GetBytes(json);
                }
                if (attack is "captured-sensitive-reservation" or "captured-sensitive-complete-orphan")
                    manifest = Encoding.UTF8.GetBytes(ReplaceRequired(Encoding.UTF8.GetString(manifest), "\"retention_file_capture_reservations\":0", "\"retention_file_capture_reservations\":1"));
                if (attack == "captured-forged-file-terminal")
                    manifest = Encoding.UTF8.GetBytes(ReplaceRequired(Encoding.UTF8.GetString(manifest), "\"retention_file_capture_reservations\":0", "\"retention_file_capture_reservations\":1"));
                if (attack == "captured-forged-journal-terminal")
                    manifest = Encoding.UTF8.GetBytes(ReplaceRequired(Encoding.UTF8.GetString(manifest), "\"retention_capture_journal\":0", "\"retention_capture_journal\":1"));
                if (attack is "captured-sdk-reservation" or "captured-sdk-sealed-orphan")
                    manifest = Encoding.UTF8.GetBytes(ReplaceRequired(Encoding.UTF8.GetString(manifest), "\"retention_analysis_sdk_directory_reservations\":0", "\"retention_analysis_sdk_directory_reservations\":1"));
                if (attack == "captured-legacy-blocker")
                    manifest = Encoding.UTF8.GetBytes(ReplaceRequired(Encoding.UTF8.GetString(manifest), "\"retention_legacy_bundle_blockers\":0", "\"retention_legacy_bundle_blockers\":1"));
                if (attack == "malformed-terminal-reservation")
                    manifest = Encoding.UTF8.GetBytes(ReplaceRequired(Encoding.UTF8.GetString(manifest), "\"retention_file_capture_reservations\":0", "\"retention_file_capture_reservations\":1"));
            }
            if (attack == "duplicate-manifest-key")
                manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"monitor\":7", "\"monitor\":7,\"monitor\":7", StringComparison.Ordinal));
            if (attack == "invalid-journal-mode")
                manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"source_journal_mode\":\"wal\"", "\"source_journal_mode\":\"other\"", StringComparison.Ordinal));
            if (attack == "invalid-external-state")
                manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"consistency\":\"ephemeral\"", "\"consistency\":\"restorable\"", StringComparison.Ordinal));
            if (attack == "invalid-retention-kind")
                manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"session_event_content\":0", "\"unexpected\":0", StringComparison.Ordinal));
            if (attack == "retention-count-mismatch")
                manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"session_event_content\":0", "\"session_event_content\":1", StringComparison.Ordinal));
            using var target = ZipFile.Open(output, ZipArchiveMode.Create);
            Write(target, "manifest.json", manifest, attack == "compressed" ? CompressionLevel.SmallestSize : CompressionLevel.NoCompression);
            var databaseName = attack == "traversal" ? "../database.sqlite" : "database.sqlite";
            var databaseEntry = Write(target, databaseName, database, CompressionLevel.NoCompression);
            if (attack == "symlink-attributes") databaseEntry.ExternalAttributes = 0xA000 << 16;
            if (attack == "duplicate") Write(target, databaseName, database, CompressionLevel.NoCompression);
            if (attack == "extra") Write(target, "extra.bin", [1], CompressionLevel.NoCompression);
            return output;
        }

        private static ZipArchiveEntry Write(ZipArchive archive, string name, byte[] bytes, CompressionLevel compression)
        {
            var entry = archive.CreateEntry(name, compression);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var stream = entry.Open();
            stream.Write(bytes);
            return entry;
        }

        private static int FindSignature(byte[] bytes, uint signature)
        {
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(encoded, signature);
            var index = bytes.AsSpan().IndexOf(encoded);
            return index >= 0 ? index : throw new InvalidDataException($"ZIP signature {signature:x8} not found.");
        }

        private static byte[] ReplaceDatabaseHash(byte[] manifest, byte[] database)
        {
            using var document = JsonDocument.Parse(manifest);
            var snapshot = document.RootElement.GetProperty("snapshot");
            var oldHash = snapshot.GetProperty("snapshot_id").GetString()!;
            var oldSize = document.RootElement.GetProperty("files")[0].GetProperty("size").GetInt64();
            var newHash = Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant();
            var json = ReplaceRequired(Encoding.UTF8.GetString(manifest), oldHash, newHash);
            json = ReplaceRequired(json, $"\"size\":{oldSize}", $"\"size\":{database.LongLength}");
            return Encoding.UTF8.GetBytes(json);
        }

        private static string ReplaceRequired(string value, string oldValue, string newValue)
        {
            if (!value.Contains(oldValue, StringComparison.Ordinal))
                throw new InvalidDataException($"Expected manifest token was absent: {oldValue}");
            return value.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        internal static byte[] Read(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        internal static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        internal static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal static long Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string ScalarString(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        internal static string[] Strings(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return [.. values];
        }

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
